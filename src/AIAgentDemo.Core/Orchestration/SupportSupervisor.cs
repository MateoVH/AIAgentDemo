using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIAgentDemo.Core.Agents;
using AIAgentDemo.Core.Costs;
using AIAgentDemo.Core.Providers;
using AIAgentDemo.Core.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgentDemo.Core.Orchestration;

/// <summary>
/// Coordinates the four agents for one customer message:
/// <list type="number">
/// <item>Classifier → structured triage (intent, language, urgency, entities).</item>
/// <item>Data Analyst → facts from the SQL store through read-only tools (skipped when not needed).</item>
/// <item>Supervisor agent → applies policy and proposes actions; sensitive ones pause for human approval.</item>
/// <item>Writer → drafts the reply in the customer's language.</item>
/// </list>
/// Control flow lives in code (predictable, testable); judgment lives in the agents.
/// Every step is traced, every model call is metered against the run budget.
/// </summary>
public sealed partial class SupportSupervisor(
    SupportAgentFactory agents,
    IApprovalGateway approvals,
    IRunStore runStore,
    IChatClientProvider provider,
    IOptions<BudgetOptions> budgetOptions,
    TimeProvider time,
    ILogger<SupportSupervisor> logger)
{
    private const int MaxApprovalRounds = 3;

    public async Task<RunResult> RunAsync(SupportRequest request, Action<RunEvent>? onEvent = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CustomerEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);

        var budget = new RunBudget(request.BudgetUsd ?? budgetOptions.Value.MaxCostPerRunUsd, budgetOptions.Value.MaxTokensPerRun);
        var run = new RunContext(request, budget, provider.Kind.ToString(), time, logger, onEvent);
        using var activity = Telemetry.ActivitySource.StartActivity("support_run");
        activity?.SetTag("agentdesk.run_id", run.RunId);
        var stopwatch = Stopwatch.StartNew();

        run.Emit(RunEventKind.RunStarted, name: provider.Kind.ToString(), detail: AgentJson.Serialize(new
        {
            request.CustomerEmail,
            request.OperatorLanguage,
            budgetUsd = budget.MaxCostUsd,
            simulated = provider.IsSimulated,
        }));

        TriageResult? triage = null;
        string? facts = null;
        string? decision = null;
        string? reply = null;
        var status = RunStatus.Completed;
        string? error = null;

        try
        {
            triage = await ClassifyAsync(run, cancellationToken);
            facts = await ResearchAsync(run, triage, cancellationToken);
            decision = await DecideAsync(run, triage, facts, cancellationToken);
            reply = await DraftReplyAsync(run, triage, facts, decision, cancellationToken);
        }
        catch (BudgetExceededException ex)
        {
            status = RunStatus.BudgetExceeded;
            error = ex.Message;
            run.Emit(RunEventKind.BudgetExceeded, detail: ex.Message, costUsd: ex.SpentUsd);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = RunStatus.Cancelled;
            error = "The run was cancelled.";
            run.Emit(RunEventKind.RunFailed, detail: error);
        }
        catch (Exception ex)
        {
            status = RunStatus.Failed;
            error = $"{ex.GetType().Name}: {ex.Message}";
            logger.LogError(ex, "[{RunId}] Run failed", run.RunId);
            run.Emit(RunEventKind.RunFailed, detail: error);
        }

        stopwatch.Stop();
        var usage = budget.Snapshot();
        run.Emit(
            RunEventKind.RunCompleted,
            name: status.ToString(),
            inputTokens: usage.InputTokens,
            outputTokens: usage.OutputTokens,
            costUsd: usage.CostUsd,
            durationMs: stopwatch.Elapsed.TotalMilliseconds);

        Telemetry.Runs.Add(1, new TagList { { "status", status.ToString() } });
        activity?.SetTag("agentdesk.status", status.ToString());
        activity?.SetTag("agentdesk.cost_usd", (double)usage.CostUsd);

        var result = new RunResult
        {
            RunId = run.RunId,
            Status = status,
            Triage = triage,
            Facts = facts,
            Decision = decision,
            Reply = reply,
            Actions = run.Actions,
            Usage = usage,
            Duration = stopwatch.Elapsed,
            Error = error,
            Events = run.Events,
        };

        try
        {
            await runStore.SaveAsync(RunRecord.From(run, result), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{RunId}] Could not persist the run log", run.RunId);
        }

        return result;
    }

    private async Task<TriageResult> ClassifyAsync(RunContext run, CancellationToken cancellationToken)
    {
        var agent = agents.Create(AgentRole.Classifier, run);
        var prompt = CasePrompts.Build(
            new CaseContext { OperatorLanguage = run.Request.OperatorLanguage, CustomerEmail = run.Request.CustomerEmail },
            run.Request.Message,
            "Classify this customer message.");

        return await StepAsync(run, AgentRole.Classifier, async () =>
        {
            TriageResult? triage;
            try
            {
                // Structured output: Agent Framework derives a JSON schema from TriageResult and deserializes the reply.
                var response = await agent.RunAsync<TriageResult>(prompt, session: null, serializerOptions: AgentJson.Options, cancellationToken: cancellationToken);
                triage = TryRead(response) ?? StructuredOutput.TryParse<TriageResult>(response.Text);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or BudgetExceededException))
            {
                // Some models or endpoints reject JSON-schema output; the instructions also ask for plain JSON.
                logger.LogWarning(ex, "[{RunId}] Structured output failed; retrying the classifier with plain JSON", run.RunId);
                var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken);
                triage = StructuredOutput.TryParse<TriageResult>(response.Text);
            }

            return Normalize(
                triage ?? throw new InvalidOperationException("The classifier did not return valid JSON."),
                run.Request.Message);
        }, AgentJson.Serialize);

        static TriageResult? TryRead(AgentResponse<TriageResult> response)
        {
            try
            {
                return response.Result;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private async Task<string?> ResearchAsync(RunContext run, TriageResult triage, CancellationToken cancellationToken)
    {
        if (!triage.NeedsDataLookup)
        {
            run.Emit(RunEventKind.Routing, AgentRole.Supervisor, "skip_data_analyst");
            return null;
        }

        run.Emit(RunEventKind.Routing, AgentRole.Supervisor, "to_data_analyst");
        var agent = agents.Create(AgentRole.DataAnalyst, run);
        var context = new CaseContext
        {
            OperatorLanguage = run.Request.OperatorLanguage,
            CustomerEmail = run.Request.CustomerEmail,
            Triage = triage,
        };

        return await StepAsync(run, AgentRole.DataAnalyst, async () =>
        {
            var response = await agent.RunAsync(
                CasePrompts.Build(context, run.Request.Message, "Gather the facts the supervisor needs to resolve this case."),
                cancellationToken: cancellationToken);
            return response.Text.Trim();
        }, facts => facts);
    }

    private async Task<string?> DecideAsync(RunContext run, TriageResult triage, string? facts, CancellationToken cancellationToken)
    {
        if (triage.Intent is SupportIntent.ProductQuestion or SupportIntent.Other)
        {
            // Informational requests never need a business action: skip a model call entirely.
            run.Emit(RunEventKind.Routing, AgentRole.Supervisor, "skip_decision");
            return null;
        }

        run.Emit(RunEventKind.Routing, AgentRole.Supervisor, "to_supervisor");
        var agent = agents.Create(AgentRole.Supervisor, run);
        var context = new CaseContext
        {
            OperatorLanguage = run.Request.OperatorLanguage,
            CustomerEmail = run.Request.CustomerEmail,
            Triage = triage,
            Facts = facts,
            Evidence = run.Evidence,
        };

        return await StepAsync(run, AgentRole.Supervisor, async () =>
        {
            // A session keeps the conversation (and the pending tool calls) between approval round-trips.
            var session = await agent.CreateSessionAsync(cancellationToken);
            var response = await agent.RunAsync(
                CasePrompts.Build(context, run.Request.Message, "Decide how to resolve this case following the policies."),
                session,
                cancellationToken: cancellationToken);

            for (var round = 0; round < MaxApprovalRounds; round++)
            {
                var pending = response.Messages
                    .SelectMany(m => m.Contents)
                    .OfType<ToolApprovalRequestContent>()
                    .ToList();
                if (pending.Count == 0)
                {
                    break;
                }

                var answers = new List<AIContent>(pending.Count);
                foreach (var request in pending)
                {
                    answers.Add(await AskHumanAsync(run, request, response.Text, cancellationToken));
                }

                response = await agent.RunAsync(new ChatMessage(ChatRole.User, answers), session, cancellationToken: cancellationToken);
            }

            return string.IsNullOrWhiteSpace(response.Text) ? null : response.Text.Trim();
        }, notes => notes);
    }

    private async Task<AIContent> AskHumanAsync(RunContext run, ToolApprovalRequestContent request, string? rationale, CancellationToken cancellationToken)
    {
        var call = request.ToolCall as FunctionCallContent;
        var toolName = call?.Name ?? "unknown_tool";
        var arguments = JsonSerializer.SerializeToElement(
            call?.Arguments ?? new Dictionary<string, object?>(),
            AgentJson.Options);

        var approvalRequest = new ApprovalRequest
        {
            RunId = run.RunId,
            RequestId = request.RequestId,
            ToolName = toolName,
            Arguments = arguments,
            CustomerEmail = run.Request.CustomerEmail,
            Rationale = string.IsNullOrWhiteSpace(rationale) ? null : rationale.Trim(),
            RequestedAt = time.GetUtcNow(),
        };

        run.Emit(RunEventKind.ApprovalRequested, AgentRole.Supervisor, toolName, AgentJson.Serialize(approvalRequest));
        var waited = Stopwatch.StartNew();
        var decision = await approvals.RequestApprovalAsync(approvalRequest, cancellationToken);
        waited.Stop();

        run.Emit(
            decision.Approved ? RunEventKind.ApprovalGranted : RunEventKind.ApprovalRejected,
            AgentRole.Supervisor,
            toolName,
            AgentJson.Serialize(new { approvalRequest.RequestId, decision.Approved, decision.Reviewer, decision.Note }),
            durationMs: waited.Elapsed.TotalMilliseconds);

        if (!decision.Approved)
        {
            run.RecordAction(new ActionOutcome(
                toolName,
                arguments,
                ActionStatus.Rejected,
                "Rejected by the human reviewer; the action was not executed.",
                RequiredApproval: true,
                ReviewerNote: decision.Note));
        }

        return request.CreateResponse(decision.Approved, decision.Note);
    }

    private async Task<string> DraftReplyAsync(RunContext run, TriageResult triage, string? facts, string? decision, CancellationToken cancellationToken)
    {
        run.Emit(RunEventKind.Routing, AgentRole.Supervisor, "to_writer");
        var agent = agents.Create(AgentRole.Writer, run);
        var context = new CaseContext
        {
            OperatorLanguage = run.Request.OperatorLanguage,
            CustomerLanguage = triage.Language,
            CustomerEmail = run.Request.CustomerEmail,
            Triage = triage,
            Facts = facts,
            Evidence = run.Evidence,
            Actions = run.Actions,
            SupervisorNotes = decision,
        };

        return await StepAsync(run, AgentRole.Writer, async () =>
        {
            var response = await agent.RunAsync(
                CasePrompts.Build(context, run.Request.Message, "Write the reply to the customer."),
                cancellationToken: cancellationToken);
            return response.Text.Trim();
        }, reply => reply);
    }

    private static async Task<T> StepAsync<T>(RunContext run, AgentRole role, Func<Task<T>> body, Func<T, string?> describe)
    {
        run.Emit(RunEventKind.AgentStarted, role);
        var stopwatch = Stopwatch.StartNew();
        var result = await body();
        stopwatch.Stop();
        run.Emit(RunEventKind.AgentCompleted, role, detail: describe(result), durationMs: stopwatch.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <summary>Deterministic guardrails on top of the model's triage.</summary>
    private static TriageResult Normalize(TriageResult triage, string message)
    {
        var language = triage.Language?.Trim().ToLowerInvariant() is { Length: >= 2 } code && code.StartsWith("es", StringComparison.Ordinal)
            ? "es"
            : "en";

        var orderId = triage.OrderId;
        if (orderId is null && ExplicitOrderNumber().Match(message) is { Success: true } match)
        {
            orderId = int.Parse(match.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return triage with { Language = language, OrderId = orderId };
    }

    [GeneratedRegex(@"(?:#|\border\s*#?|\bpedido\s*#?)\s*(?<id>\d{4,6})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitOrderNumber();
}
