using System.Collections.Concurrent;
using AIAgentDemo.Core.Orchestration;
using Microsoft.Extensions.Options;

namespace AIAgentDemo.Web.Services;

/// <summary>
/// Human-in-the-loop gateway for the web console. The run awaits a decision; the approval card
/// in the UI resolves it. Undecided requests are rejected after <see cref="ApprovalOptions.TimeoutMinutes"/>.
/// </summary>
public sealed class PendingApprovals(IOptions<ApprovalOptions> options, ILogger<PendingApprovals> logger) : IApprovalGateway
{
    public const string OperatorReviewer = "operator";

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalDecision>> _pending = new();

    public async Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(options.Value.TimeoutMinutes);
        var decision = Slot(request.RequestId);
        try
        {
            return await decision.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("[{RunId}] Approval {RequestId} timed out after {Timeout}", request.RunId, request.RequestId, timeout);
            return new ApprovalDecision(false, "system", $"No decision within {options.Value.TimeoutMinutes:0.#} minutes; rejected automatically.");
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    /// <summary>Records the operator's decision. Safe even if the click lands before the run starts waiting.</summary>
    public bool Resolve(string requestId, bool approved, string? note) =>
        Slot(requestId).TrySetResult(new ApprovalDecision(approved, OperatorReviewer, string.IsNullOrWhiteSpace(note) ? null : note.Trim()));

    private TaskCompletionSource<ApprovalDecision> Slot(string requestId) =>
        _pending.GetOrAdd(requestId, _ => new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously));
}
