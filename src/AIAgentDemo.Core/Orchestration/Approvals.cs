using System.Text.Json;

namespace AIAgentDemo.Core.Orchestration;

public sealed class ApprovalOptions
{
    public static readonly string[] DefaultRequiredFor = ["issue_refund", "create_discount_coupon", "cancel_order"];

    /// <summary>Tools that pause the run until a human approves them. Empty → <see cref="DefaultRequiredFor"/>.</summary>
    public string[] RequiredFor { get; set; } = [];

    /// <summary>Pending approvals are rejected automatically after this many minutes.</summary>
    public double TimeoutMinutes { get; set; } = 15;

    public IReadOnlySet<string> EffectiveRequiredFor =>
        new HashSet<string>(RequiredFor.Length > 0 ? RequiredFor : DefaultRequiredFor, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A sensitive action waiting for a human decision.</summary>
public sealed record ApprovalRequest
{
    public required string RunId { get; init; }

    public required string RequestId { get; init; }

    public required string ToolName { get; init; }

    public required JsonElement Arguments { get; init; }

    public required string CustomerEmail { get; init; }

    /// <summary>What the Supervisor agent said before proposing the action, if anything.</summary>
    public string? Rationale { get; init; }

    public DateTimeOffset RequestedAt { get; init; }
}

public sealed record ApprovalDecision(bool Approved, string Reviewer, string? Note = null);

/// <summary>Human-in-the-loop seam: the web app shows an approval card, tests auto-decide.</summary>
public interface IApprovalGateway
{
    Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Decides every approval the same way. Used by tests and headless runs.</summary>
public sealed class AutoApprovalGateway(bool approve, string reviewer = "auto-reviewer") : IApprovalGateway
{
    private readonly List<ApprovalRequest> _requests = [];

    public IReadOnlyList<ApprovalRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToArray();
            }
        }
    }

    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        return Task.FromResult(new ApprovalDecision(approve, reviewer, approve ? null : "Rejected by policy in automated run."));
    }
}
