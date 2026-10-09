using AIAgentDemo.Core.Costs;
using AIAgentDemo.Core.Tracing;

namespace AIAgentDemo.Core.Orchestration;

public enum RunStatus
{
    Completed,
    BudgetExceeded,
    Failed,
    Cancelled,
}

public sealed record RunResult
{
    public required string RunId { get; init; }

    public required RunStatus Status { get; init; }

    public TriageResult? Triage { get; init; }

    /// <summary>Facts reported by the Data Analyst (operator language).</summary>
    public string? Facts { get; init; }

    /// <summary>The Supervisor's decision notes (operator language).</summary>
    public string? Decision { get; init; }

    /// <summary>Draft reply for the customer (customer language), to be reviewed by the operator.</summary>
    public string? Reply { get; init; }

    public IReadOnlyList<ActionOutcome> Actions { get; init; } = [];

    public required UsageSummary Usage { get; init; }

    public TimeSpan Duration { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<RunEvent> Events { get; init; } = [];
}
