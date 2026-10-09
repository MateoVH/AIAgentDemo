namespace AIAgentDemo.Core.Orchestration;

/// <summary>A customer message entering the support pipeline.</summary>
public sealed record SupportRequest
{
    /// <summary>Customer the case belongs to. Set by the operator, so it is trusted; actions are scoped to it.</summary>
    public required string CustomerEmail { get; init; }

    /// <summary>Raw customer message. Untrusted input: it is fenced in prompts and never treated as instructions.</summary>
    public required string Message { get; init; }

    /// <summary>Language of the human operator ("es" or "en"). Internal notes are written in it.</summary>
    public string OperatorLanguage { get; init; } = "en";

    /// <summary>Optional per-run budget override in USD.</summary>
    public decimal? BudgetUsd { get; init; }

    public string RunId { get; init; } = RunIds.New();
}

public static class RunIds
{
    public static string New() => "run_" + Guid.NewGuid().ToString("N")[..10];
}
