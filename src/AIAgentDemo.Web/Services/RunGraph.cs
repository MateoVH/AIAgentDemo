using System.Text.Json;
using AIAgentDemo.Core;
using AIAgentDemo.Core.Agents;
using AIAgentDemo.Core.Orchestration;
using AIAgentDemo.Core.Tracing;

namespace AIAgentDemo.Web.Services;

/// <summary>Lifelines of the run graph: the four agents plus the human reviewer.</summary>
public enum Lane
{
    Classifier = 0,
    DataAnalyst = 1,
    Supervisor = 2,
    Writer = 3,
    Human = 4,
}

public abstract class GraphRow
{
    public required string Key { get; init; }

    /// <summary>Lanes whose lifeline is "lit" on this row (an agent working, or a pending approval).</summary>
    public IReadOnlySet<Lane> ActiveLanes { get; set; } = new HashSet<Lane>();
}

public sealed class HandoffRow : GraphRow
{
    public required Lane From { get; init; }

    public required Lane To { get; init; }

    public required string Route { get; init; }
}

public sealed class SkipRow : GraphRow
{
    public required Lane Lane { get; init; }

    public required string Route { get; init; }
}

public sealed record ModelCall(string Model, long InputTokens, long OutputTokens, decimal CostUsd, double DurationMs, string? FinishReason);

public sealed class ToolInteraction
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public string? Arguments { get; init; }

    public string? Result { get; set; }

    public string? Error { get; set; }

    public double? DurationMs { get; set; }

    public bool Pending => Result is null && Error is null;
}

public enum ActivationState
{
    Working,
    WaitingForHuman,
    Done,
}

/// <summary>One stretch of work by an agent. An approval splits the Supervisor into two activations.</summary>
public sealed class ActivationRow : GraphRow
{
    public required Lane Lane { get; init; }

    public bool IsContinuation { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public ActivationState State { get; set; } = ActivationState.Working;

    /// <summary>Model calls (<see cref="ModelCall"/>) and tool calls (<see cref="ToolInteraction"/>) in the order they happened.</summary>
    public List<object> Steps { get; } = [];

    public string? Output { get; set; }

    public double? DurationMs { get; set; }

    public decimal CostUsd => Steps.OfType<ModelCall>().Sum(c => c.CostUsd);
}

public sealed class ApprovalRow : GraphRow
{
    public required ApprovalRequest Request { get; init; }

    /// <summary>The activation that paused for this decision.</summary>
    public ActivationRow? Requester { get; init; }

    public ApprovalDecision? Decision { get; set; }

    public double? WaitedMs { get; set; }
}

public enum NoticeKind
{
    BudgetExceeded,
    Failed,
    Completed,
}

public sealed class NoticeRow : GraphRow
{
    public required NoticeKind Kind { get; init; }

    public string? Detail { get; init; }

    public RunEvent? Source { get; init; }

    public decimal BudgetUsd { get; init; }
}

/// <summary>Turns the flat event log into rows of the run graph (hand-offs, activations, approvals).</summary>
public static class RunGraph
{
    public static IReadOnlyList<GraphRow> Build(IReadOnlyList<RunEvent> events)
    {
        var rows = new List<GraphRow>();
        var open = new Dictionary<Lane, ActivationRow>();
        var pendingApprovals = new Dictionary<string, ApprovalRow>();
        var lastLane = Lane.Classifier;
        ActivationRow? lastWaiting = null;
        DateTimeOffset? resumedAt = null;
        decimal budgetUsd = 0;

        void Add(GraphRow row) => rows.Add(row);

        ActivationRow Current(Lane lane, RunEvent e)
        {
            if (open.TryGetValue(lane, out var activation))
            {
                return activation;
            }

            // Work after an approval decision: the agent picks the case up again.
            activation = new ActivationRow { Key = $"a{e.Sequence}", Lane = lane, IsContinuation = true, StartedAt = resumedAt ?? e.Timestamp };
            open[lane] = activation;
            Add(activation);
            return activation;
        }

        foreach (var e in events)
        {
            var lane = e.Agent is { } agent ? (Lane)(int)agent : Lane.Supervisor;
            switch (e.Kind)
            {
                case RunEventKind.RunStarted:
                    budgetUsd = RunInsights.TryParse(e.Detail) is { } run && run.TryGetProperty("budgetUsd", out var b) && b.TryGetDecimal(out var limit) ? limit : 0;
                    break;

                case RunEventKind.Routing when e.Name is { } route && route.StartsWith("skip_", StringComparison.Ordinal):
                    Add(new SkipRow { Key = $"r{e.Sequence}", Lane = route == "skip_data_analyst" ? Lane.DataAnalyst : Lane.Supervisor, Route = route });
                    break;

                case RunEventKind.Routing when e.Name is { } route:
                    var to = route switch
                    {
                        "to_data_analyst" => Lane.DataAnalyst,
                        "to_supervisor" => Lane.Supervisor,
                        _ => Lane.Writer,
                    };
                    Add(new HandoffRow { Key = $"r{e.Sequence}", From = lastLane, To = to, Route = route });
                    break;

                case RunEventKind.AgentStarted:
                    var started = new ActivationRow { Key = $"a{e.Sequence}", Lane = lane, StartedAt = e.Timestamp };
                    open[lane] = started;
                    Add(started);
                    break;

                case RunEventKind.LlmCall:
                    var detail = RunInsights.TryParse(e.Detail);
                    var finish = detail is { } d && d.TryGetProperty("finishReason", out var f) ? f.GetString() : null;
                    Current(lane, e).Steps.Add(new ModelCall(e.Name ?? "?", e.InputTokens ?? 0, e.OutputTokens ?? 0, e.CostUsd ?? 0, e.DurationMs ?? 0, finish));
                    break;

                case RunEventKind.ToolCall:
                    Current(lane, e).Steps.Add(new ToolInteraction { Key = $"t{e.Sequence}", Name = e.Name ?? "?", Arguments = e.Detail });
                    break;

                case RunEventKind.ToolResult or RunEventKind.ToolError:
                    var call = Current(lane, e).Steps.OfType<ToolInteraction>().LastOrDefault(t => t.Name == e.Name && t.Pending);
                    if (call is not null)
                    {
                        call.DurationMs = e.DurationMs;
                        if (e.Kind == RunEventKind.ToolResult)
                        {
                            call.Result = e.Detail;
                        }
                        else
                        {
                            call.Error = e.Detail;
                        }
                    }

                    break;

                case RunEventKind.ApprovalRequested:
                    if (open.Remove(lane, out var waiting))
                    {
                        // Time the agent spent working, excluding the human wait that follows.
                        waiting.State = ActivationState.WaitingForHuman;
                        waiting.DurationMs = (e.Timestamp - waiting.StartedAt).TotalMilliseconds;
                        lastWaiting = waiting;
                    }

                    if (e.Detail is { } json && JsonSerializer.Deserialize<ApprovalRequest>(json, AgentJson.Options) is { } request)
                    {
                        var approval = new ApprovalRow { Key = $"p{e.Sequence}", Request = request, Requester = lastWaiting };
                        pendingApprovals[request.RequestId] = approval;
                        Add(approval);
                    }

                    break;

                case RunEventKind.ApprovalGranted or RunEventKind.ApprovalRejected:
                    var decidedId = RunInsights.RequestId(e.Detail)
                        ?? pendingApprovals.Values.FirstOrDefault(p => p.Request.ToolName == e.Name)?.Request.RequestId;
                    if (decidedId is not null && pendingApprovals.Remove(decidedId, out var decided))
                    {
                        decided.Decision = e.Detail is { } decision ? JsonSerializer.Deserialize<ApprovalDecision>(decision, AgentJson.Options) : null;
                        decided.WaitedMs = e.DurationMs;
                        resumedAt = e.Timestamp;
                        if (decided.Requester is { } requester)
                        {
                            requester.State = ActivationState.Done;
                        }
                    }

                    break;

                case RunEventKind.AgentCompleted:
                    var done = open.Remove(lane, out var active)
                        ? active
                        : rows.OfType<ActivationRow>().LastOrDefault(a => a.Lane == lane);
                    if (done is not null)
                    {
                        done.State = ActivationState.Done;
                        done.Output = e.Detail;
                        done.DurationMs = (e.Timestamp - done.StartedAt).TotalMilliseconds;
                    }

                    lastLane = lane;
                    break;

                case RunEventKind.BudgetExceeded:
                    CloseAll(e.Timestamp);
                    Add(new NoticeRow { Key = $"n{e.Sequence}", Kind = NoticeKind.BudgetExceeded, Detail = e.Detail, Source = e, BudgetUsd = budgetUsd });
                    break;

                case RunEventKind.RunFailed:
                    CloseAll(e.Timestamp);
                    Add(new NoticeRow { Key = $"n{e.Sequence}", Kind = NoticeKind.Failed, Detail = e.Detail, Source = e });
                    break;

                case RunEventKind.RunCompleted when e.Name == nameof(RunStatus.Completed):
                    Add(new NoticeRow { Key = $"n{e.Sequence}", Kind = NoticeKind.Completed, Source = e });
                    break;
            }

            // Light the lifelines that were busy while the newest row was being written.
            if (rows.Count > 0)
            {
                var lit = rows[^1].ActiveLanes.ToHashSet();
                lit.UnionWith(open.Keys);
                if (pendingApprovals.Count > 0)
                {
                    lit.Add(Lane.Human);
                }

                rows[^1].ActiveLanes = lit;
            }
        }

        return rows;

        void CloseAll(DateTimeOffset at)
        {
            foreach (var activation in open.Values)
            {
                activation.State = ActivationState.Done;
                activation.DurationMs = (at - activation.StartedAt).TotalMilliseconds;
            }

            open.Clear();
        }
    }
}
