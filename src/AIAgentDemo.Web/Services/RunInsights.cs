using System.Text.Json;
using AIAgentDemo.Core;
using AIAgentDemo.Core.Agents;
using AIAgentDemo.Core.Orchestration;
using AIAgentDemo.Core.Tracing;

namespace AIAgentDemo.Web.Services;

public sealed record ActionView(string Tool, JsonElement Arguments, ActionStatus Status, string? Message, string? Reference, string? Note);

public sealed record SpendRow(AgentRole Agent, string Model, int Calls, long InputTokens, long OutputTokens, decimal CostUsd);

/// <summary>
/// Everything the side panels show, derived from the event log alone. The live console and the
/// history replay read the same events, so both views can never disagree.
/// </summary>
public sealed class RunInsights
{
    public bool Simulated { get; private set; }

    public decimal BudgetUsd { get; private set; }

    public TriageResult? Triage { get; private set; }

    public string? Reply { get; private set; }

    public List<ActionView> Actions { get; } = [];

    public List<SpendRow> Spend { get; } = [];

    public int Calls => Spend.Sum(s => s.Calls);

    public long InputTokens => Spend.Sum(s => s.InputTokens);

    public long OutputTokens => Spend.Sum(s => s.OutputTokens);

    public decimal CostUsd => Spend.Sum(s => s.CostUsd);

    public RunStatus? FinalStatus { get; private set; }

    public string? BudgetMessage { get; private set; }

    public bool AwaitingApproval { get; private set; }

    public IReadOnlyList<string> Models => Spend.Select(s => s.Model).Distinct().ToList();

    public static RunInsights From(IReadOnlyList<RunEvent> events)
    {
        var insights = new RunInsights();
        var spend = new Dictionary<AgentRole, SpendRow>();
        var lastArguments = new Dictionary<string, JsonElement>();
        var pending = new Dictionary<string, (string Tool, JsonElement Arguments)>();

        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case RunEventKind.RunStarted:
                    if (TryParse(e.Detail) is { } started)
                    {
                        insights.Simulated = started.TryGetProperty("simulated", out var s) && s.ValueKind == JsonValueKind.True;
                        insights.BudgetUsd = started.TryGetProperty("budgetUsd", out var b) && b.TryGetDecimal(out var budget) ? budget : 0m;
                    }

                    break;

                case RunEventKind.LlmCall when e.Agent is { } agent:
                    spend[agent] = spend.TryGetValue(agent, out var row)
                        ? row with
                        {
                            Calls = row.Calls + 1,
                            InputTokens = row.InputTokens + (e.InputTokens ?? 0),
                            OutputTokens = row.OutputTokens + (e.OutputTokens ?? 0),
                            CostUsd = row.CostUsd + (e.CostUsd ?? 0),
                        }
                        : new SpendRow(agent, e.Name ?? "?", 1, e.InputTokens ?? 0, e.OutputTokens ?? 0, e.CostUsd ?? 0);
                    break;

                case RunEventKind.AgentCompleted:
                    switch (e.Agent)
                    {
                        case AgentRole.Classifier:
                            insights.Triage = StructuredOutput.TryParse<TriageResult>(e.Detail);
                            break;
                        case AgentRole.Writer:
                            insights.Reply = e.Detail;
                            break;
                    }

                    break;

                case RunEventKind.ToolCall when e.Agent == AgentRole.Supervisor && e.Name is { } tool:
                    if (TryParse(e.Detail) is { } arguments)
                    {
                        lastArguments[tool] = arguments;
                    }

                    break;

                case RunEventKind.ToolResult when e.Agent == AgentRole.Supervisor && e.Name is { } tool:
                    var result = TryParse(e.Detail);
                    var success = result is { } r && r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True;
                    insights.Actions.Add(new ActionView(
                        tool,
                        lastArguments.GetValueOrDefault(tool),
                        success ? ActionStatus.Executed : ActionStatus.Failed,
                        String(result, "message"),
                        String(result, "reference"),
                        null));
                    break;

                case RunEventKind.ApprovalRequested when e.Name is { } tool && RequestId(e.Detail) is { } requestId:
                    var requested = TryParse(e.Detail) is { } request && request.TryGetProperty("arguments", out var a) ? a.Clone() : default;
                    pending[requestId] = (tool, requested);
                    break;

                case RunEventKind.ApprovalGranted:
                    Resolve(pending, e);
                    break;

                case RunEventKind.ApprovalRejected when e.Name is { } tool:
                    insights.Actions.Add(new ActionView(
                        tool,
                        Resolve(pending, e)?.Arguments ?? default,
                        ActionStatus.Rejected,
                        null,
                        null,
                        String(TryParse(e.Detail), "note")));
                    break;

                case RunEventKind.BudgetExceeded:
                    insights.BudgetMessage = e.Detail;
                    break;

                case RunEventKind.RunCompleted:
                    insights.FinalStatus = Enum.TryParse<RunStatus>(e.Name, out var status) ? status : null;
                    break;
            }
        }

        insights.Spend.AddRange(spend.Values.OrderBy(s => s.Agent));
        insights.AwaitingApproval = pending.Count > 0 && insights.FinalStatus is null;
        return insights;
    }

    /// <summary>The approval request id carried by approval events (null for logs written before it existed).</summary>
    internal static string? RequestId(string? json) => String(TryParse(json), "requestId");

    private static (string Tool, JsonElement Arguments)? Resolve(Dictionary<string, (string Tool, JsonElement Arguments)> pending, RunEvent decision)
    {
        var id = RequestId(decision.Detail) ?? pending.FirstOrDefault(p => p.Value.Tool == decision.Name).Key;
        return id is not null && pending.Remove(id, out var request) ? request : null;
    }

    internal static JsonElement? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json[0] is not ('{' or '['))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
