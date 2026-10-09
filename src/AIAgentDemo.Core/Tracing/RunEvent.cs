using AIAgentDemo.Core.Agents;

namespace AIAgentDemo.Core.Tracing;

public enum RunEventKind
{
    RunStarted,
    Routing,
    AgentStarted,
    AgentCompleted,
    LlmCall,
    ToolCall,
    ToolResult,
    ToolError,
    ApprovalRequested,
    ApprovalGranted,
    ApprovalRejected,
    BudgetExceeded,
    RunCompleted,
    RunFailed,
}

/// <summary>
/// One step of a run. Every LLM call, tool call, approval and routing decision becomes an event,
/// which is streamed to the UI, logged, and persisted for the history view.
/// </summary>
public sealed record RunEvent
{
    public required string RunId { get; init; }

    public required int Sequence { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required RunEventKind Kind { get; init; }

    public AgentRole? Agent { get; init; }

    /// <summary>Tool name, model id or routing key, depending on <see cref="Kind"/>.</summary>
    public string? Name { get; init; }

    /// <summary>Payload: JSON for tool calls/results/approvals, text for agent outputs.</summary>
    public string? Detail { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public decimal? CostUsd { get; init; }

    public double? DurationMs { get; init; }
}
