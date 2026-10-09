using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using AIAgentDemo.Core.Data;
using AIAgentDemo.Core.Orchestration;
using Dapper;
using Microsoft.Extensions.AI;

namespace AIAgentDemo.Core.Tools;

/// <summary>Result returned to the model by an action tool.</summary>
public sealed record ActionResult(bool Success, string Message, string? Reference = null);

/// <summary>
/// Side-effecting tools for the Supervisor agent. Business rules are enforced here, in code,
/// not only in the prompt: actions are scoped to the case's customer, refunds cannot exceed what
/// was paid, shipped orders cannot be cancelled. Human approval is layered on top of these checks.
/// </summary>
public sealed class ActionTools(StoreDatabase db, RunContext run, TimeProvider time, IReadOnlySet<string> approvalRequired)
{
    public const string IssueRefund = "issue_refund";
    public const string CreateDiscountCoupon = "create_discount_coupon";
    public const string CancelOrder = "cancel_order";
    public const string EscalateToHuman = "escalate_to_human";

    private static readonly string[] Priorities = ["low", "normal", "high", "urgent"];

    public IReadOnlyList<AIFunction> CreateFunctions() =>
    [
        AIFunctionFactory.Create(
            async (
                [Description("Order id")] int orderId,
                [Description("Amount to refund in USD; at most the order total minus previous refunds")] decimal amount,
                [Description("Short reason, e.g. 'Item arrived damaged'")] string reason,
                CancellationToken ct) => await IssueRefundAsync(orderId, amount, reason, ct),
            IssueRefund,
            Describe(IssueRefund, "Refunds money to the original payment method for a delivered order of this customer."),
            AgentJson.Options),

        AIFunctionFactory.Create(
            async (
                [Description("Email of the customer receiving the coupon (must be the case customer)")] string customerEmail,
                [Description("Discount percentage between 5 and 25")] int percent,
                [Description("Short reason, e.g. 'Delayed delivery'")] string reason,
                CancellationToken ct) => await CreateCouponAsync(customerEmail, percent, reason, ct),
            CreateDiscountCoupon,
            Describe(CreateDiscountCoupon, "Creates a single-use discount coupon (valid 90 days) as a goodwill gesture."),
            AgentJson.Options),

        AIFunctionFactory.Create(
            async (
                [Description("Order id")] int orderId,
                [Description("Short reason given by the customer")] string reason,
                CancellationToken ct) => await CancelOrderAsync(orderId, reason, ct),
            CancelOrder,
            Describe(CancelOrder, "Cancels an order that has not shipped yet (status 'pending' or 'paid'); the payment is returned in full."),
            AgentJson.Options),

        AIFunctionFactory.Create(
            async (
                [Description("Order id related to the case, or null")] int? orderId,
                [Description("low, normal, high or urgent")] string priority,
                [Description("What the specialist needs to know")] string summary,
                CancellationToken ct) => await EscalateAsync(orderId, priority, summary, ct),
            EscalateToHuman,
            Describe(EscalateToHuman, "Opens a ticket so a senior human specialist follows up with the customer."),
            AgentJson.Options),
    ];

    internal async Task<ActionResult> IssueRefundAsync(int orderId, decimal amount, string reason, CancellationToken cancellationToken)
    {
        var arguments = AgentJson.ToElement(new { orderId, amount, reason });
        await using var connection = await db.OpenAsync(cancellationToken);
        var order = await FindOrderAsync(connection, orderId);

        if (order is null)
        {
            return Fail(IssueRefund, arguments, $"Order {orderId} does not exist.");
        }

        if (!BelongsToCase(order.Email))
        {
            return Fail(IssueRefund, arguments, $"Order {orderId} does not belong to the customer of this case.");
        }

        if (order.Status is not ("delivered" or "partially_refunded"))
        {
            return Fail(IssueRefund, arguments, $"Only delivered orders can be refunded; order {orderId} is '{order.Status}'.");
        }

        amount = decimal.Round(amount, 2);
        var remaining = decimal.Round(order.Total - order.Refunded, 2);
        if (amount <= 0 || amount > remaining)
        {
            return Fail(IssueRefund, arguments, $"Refund amount must be between $0.01 and {Money.Usd(remaining)} for order {orderId}.");
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var refundId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO refunds (order_id, amount, reason, approved_by, run_id, created_at)
            VALUES (@orderId, @amount, @reason, @approvedBy, @runId, @now)
            RETURNING id
            """,
            new { orderId, amount = (double)amount, reason, approvedBy = ApprovedBy(IssueRefund), runId = run.RunId, now = Now() },
            transaction);
        var newStatus = amount == remaining ? "refunded" : "partially_refunded";
        await connection.ExecuteAsync(
            "UPDATE orders SET status = @newStatus WHERE id = @orderId",
            new { newStatus, orderId },
            transaction);
        await transaction.CommitAsync(cancellationToken);

        return Succeed(IssueRefund, arguments, $"Refunded {Money.Usd(amount)} for order {orderId}; order status is now '{newStatus}'.", $"RF-{refundId:D5}");
    }

    internal async Task<ActionResult> CreateCouponAsync(string customerEmail, int percent, string reason, CancellationToken cancellationToken)
    {
        var arguments = AgentJson.ToElement(new { customerEmail, percent, reason });
        if (!BelongsToCase(customerEmail))
        {
            return Fail(CreateDiscountCoupon, arguments, "Coupons can only be issued to the customer of this case.");
        }

        if (percent is < 5 or > 25)
        {
            return Fail(CreateDiscountCoupon, arguments, "Coupon percent must be between 5 and 25.");
        }

        await using var connection = await db.OpenAsync(cancellationToken);
        var customerId = await connection.ExecuteScalarAsync<long?>(
            "SELECT id FROM customers WHERE lower(email) = lower(@customerEmail)",
            new { customerEmail = customerEmail.Trim() });
        if (customerId is null)
        {
            return Fail(CreateDiscountCoupon, arguments, $"No customer found with email '{customerEmail}'.");
        }

        var code = "NOVA-" + RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 6);
        await connection.ExecuteAsync(
            """
            INSERT INTO coupons (code, customer_id, percent, reason, approved_by, run_id, expires_at, created_at)
            VALUES (@code, @customerId, @percent, @reason, @approvedBy, @runId, @expires, @now)
            """,
            new
            {
                code, customerId, percent, reason, approvedBy = ApprovedBy(CreateDiscountCoupon), runId = run.RunId,
                expires = Now(TimeSpan.FromDays(90)), now = Now(),
            });

        return Succeed(CreateDiscountCoupon, arguments, $"Created coupon {code}: {percent}% off, valid for 90 days.", code);
    }

    internal async Task<ActionResult> CancelOrderAsync(int orderId, string reason, CancellationToken cancellationToken)
    {
        var arguments = AgentJson.ToElement(new { orderId, reason });
        await using var connection = await db.OpenAsync(cancellationToken);
        var order = await FindOrderAsync(connection, orderId);

        if (order is null)
        {
            return Fail(CancelOrder, arguments, $"Order {orderId} does not exist.");
        }

        if (!BelongsToCase(order.Email))
        {
            return Fail(CancelOrder, arguments, $"Order {orderId} does not belong to the customer of this case.");
        }

        if (order.Status is not ("pending" or "paid"))
        {
            return Fail(CancelOrder, arguments, $"Order {orderId} is '{order.Status}' and can no longer be cancelled.");
        }

        await connection.ExecuteAsync("UPDATE orders SET status = 'cancelled' WHERE id = @orderId", new { orderId });
        return Succeed(CancelOrder, arguments, $"Order {orderId} cancelled; {Money.Usd(order.Total)} will be returned to the original payment method.", $"CX-{orderId}");
    }

    internal async Task<ActionResult> EscalateAsync(int? orderId, string priority, string summary, CancellationToken cancellationToken)
    {
        var arguments = AgentJson.ToElement(new { orderId, priority, summary });
        priority = Priorities.Contains(priority?.Trim().ToLowerInvariant()) ? priority!.Trim().ToLowerInvariant() : "normal";

        await using var connection = await db.OpenAsync(cancellationToken);
        if (orderId is int id)
        {
            var order = await FindOrderAsync(connection, id);
            if (order is null || !BelongsToCase(order.Email))
            {
                return Fail(EscalateToHuman, arguments, $"Order {id} does not belong to the customer of this case.");
            }
        }

        var ticketId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO support_tickets (customer_id, order_id, priority, summary, run_id, created_at)
            VALUES ((SELECT id FROM customers WHERE lower(email) = lower(@email)), @orderId, @priority, @summary, @runId, @now)
            RETURNING id
            """,
            new { email = run.Request.CustomerEmail.Trim(), orderId, priority, summary, runId = run.RunId, now = Now() });

        return Succeed(EscalateToHuman, arguments, $"Ticket created with {priority} priority; a specialist will contact the customer within 24 hours.", $"TCK-{ticketId:D4}");
    }

    private string Describe(string tool, string description) =>
        approvalRequired.Contains(tool)
            ? description + " A human reviewer must approve this action before it runs."
            : description;

    private bool BelongsToCase(string email) =>
        string.Equals(email.Trim(), run.Request.CustomerEmail.Trim(), StringComparison.OrdinalIgnoreCase);

    private string ApprovedBy(string tool) => approvalRequired.Contains(tool) ? "human-reviewer" : "auto-policy";

    private string Now(TimeSpan offset = default) =>
        time.GetUtcNow().Add(offset).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private ActionResult Succeed(string tool, System.Text.Json.JsonElement arguments, string message, string reference)
    {
        run.RecordAction(new ActionOutcome(tool, arguments, ActionStatus.Executed, message, reference, approvalRequired.Contains(tool)));
        return new ActionResult(true, message, reference);
    }

    private ActionResult Fail(string tool, System.Text.Json.JsonElement arguments, string message)
    {
        run.RecordAction(new ActionOutcome(tool, arguments, ActionStatus.Failed, message, null, approvalRequired.Contains(tool)));
        return new ActionResult(false, message);
    }

    private static Task<OrderRow?> FindOrderAsync(System.Data.IDbConnection connection, int orderId) =>
        connection.QuerySingleOrDefaultAsync<OrderRow>(
            """
            SELECT o.id AS Id, o.status AS Status, o.total AS Total, c.email AS Email,
                   (SELECT IFNULL(SUM(r.amount), 0.0) FROM refunds r WHERE r.order_id = o.id) AS Refunded
            FROM orders o JOIN customers c ON c.id = o.customer_id
            WHERE o.id = @orderId
            """,
            new { orderId });

    private sealed record OrderRow
    {
        public int Id { get; init; }
        public string Status { get; init; } = "";
        public decimal Total { get; init; }
        public string Email { get; init; } = "";
        public decimal Refunded { get; init; }
    }
}
