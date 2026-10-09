using System.Text.Json;

namespace AIAgentDemo.Core.Orchestration;

public enum ActionStatus
{
    /// <summary>The tool ran and the business rules accepted it.</summary>
    Executed,

    /// <summary>The tool ran but a business rule refused it (e.g. amount above the order total).</summary>
    Failed,

    /// <summary>A human reviewer rejected the action, so it never ran.</summary>
    Rejected,
}

/// <summary>What happened to an action the Supervisor agent proposed.</summary>
public sealed record ActionOutcome(
    string Tool,
    JsonElement Arguments,
    ActionStatus Status,
    string Message,
    string? Reference = null,
    bool RequiredApproval = false,
    string? ReviewerNote = null);

/// <summary>A raw tool result gathered by the Data Analyst, passed downstream as evidence.</summary>
public sealed record ToolEvidence(string Tool, JsonElement Arguments, JsonElement Result);
