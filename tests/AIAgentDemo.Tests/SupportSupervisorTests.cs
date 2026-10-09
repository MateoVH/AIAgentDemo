using AIAgentDemo.Core.Agents;
using AIAgentDemo.Core.Orchestration;
using AIAgentDemo.Core.Tools;
using AIAgentDemo.Core.Tracing;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace AIAgentDemo.Tests;

/// <summary>
/// End-to-end runs of the four Agent Framework agents (simulated model): routing, tool calls,
/// human approval round-trips, budget enforcement and the persisted audit log.
/// </summary>
public class SupportSupervisorTests
{
    [Fact]
    public async Task Damaged_item_refund_waits_for_approval_then_executes()
    {
        await using var host = await TestHost.StartAsync(approve: true);

        var result = await host.RunSampleAsync("damaged", operatorLanguage: "es");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(SupportIntent.DamagedItem, result.Triage!.Intent);

        var approval = Assert.Single(host.Approvals.Requests);
        Assert.Equal(ActionTools.IssueRefund, approval.ToolName);
        Assert.Equal(149.50m, approval.Arguments.GetProperty("amount").GetDecimal());

        var refund = Assert.Single(result.Actions);
        Assert.Equal(ActionStatus.Executed, refund.Status);
        Assert.StartsWith("RF-", refund.Reference);

        await using var connection = await host.Database.OpenAsync();
        Assert.Equal(149.50m, await connection.ExecuteScalarAsync<decimal>("SELECT amount FROM refunds WHERE order_id = 1001"));
        Assert.Equal("refunded", await connection.ExecuteScalarAsync<string>("SELECT status FROM orders WHERE id = 1001"));

        Assert.Contains("reembolso", result.Reply);
        Assert.Contains(refund.Reference!, result.Reply);
    }

    [Fact]
    public async Task Rejected_approval_never_touches_the_database_and_the_reply_does_not_promise_it()
    {
        await using var host = await TestHost.StartAsync(approve: false);

        var result = await host.RunSampleAsync("damaged");

        Assert.Equal(RunStatus.Completed, result.Status);
        var action = Assert.Single(result.Actions);
        Assert.Equal(ActionStatus.Rejected, action.Status);
        Assert.Contains(result.Events, e => e.Kind == RunEventKind.ApprovalRejected);

        await using var connection = await host.Database.OpenAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM refunds"));
        Assert.DoesNotContain("RF-", result.Reply);
        Assert.Contains("especialista", result.Reply);
    }

    [Fact]
    public async Task Product_question_skips_the_decision_step_and_answers_from_the_catalog()
    {
        await using var host = await TestHost.StartAsync();

        var result = await host.RunSampleAsync("product");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Empty(host.Approvals.Requests);
        Assert.Contains(result.Events, e => e.Kind == RunEventKind.Routing && e.Name == "skip_decision");
        Assert.DoesNotContain(result.Events, e => e.Kind == RunEventKind.LlmCall && e.Agent == AgentRole.Supervisor);
        Assert.Contains(result.Events, e => e.Kind == RunEventKind.ToolCall && e.Name == "search_products");
        Assert.Contains("Pulse Smartwatch S2", result.Reply);
        Assert.Contains("$189.00", result.Reply);
        Assert.StartsWith("Hi Sophie", result.Reply);
    }

    [Fact]
    public async Task Late_delivery_gets_an_apology_coupon()
    {
        await using var host = await TestHost.StartAsync();

        var result = await host.RunSampleAsync("late");

        var coupon = Assert.Single(result.Actions);
        Assert.Equal(ActionTools.CreateDiscountCoupon, coupon.Tool);
        Assert.Equal(ActionStatus.Executed, coupon.Status);
        Assert.Contains(coupon.Reference!, result.Reply);
        Assert.Contains("DHL Express", result.Reply);

        await using var connection = await host.Database.OpenAsync();
        Assert.Equal(10, await connection.ExecuteScalarAsync<int>("SELECT percent FROM coupons"));
    }

    [Fact]
    public async Task Missing_item_refunds_only_that_item()
    {
        await using var host = await TestHost.StartAsync();

        var result = await host.RunSampleAsync("missing");

        var refund = Assert.Single(result.Actions);
        Assert.Equal(29.90m, refund.Arguments.GetProperty("amount").GetDecimal());
        Assert.Equal("partially_refunded", (await host.Repository.GetOrderDetailsAsync(1005))!.Status);
    }

    [Fact]
    public async Task Cancellation_of_an_unshipped_order_is_executed_after_approval()
    {
        await using var host = await TestHost.StartAsync();

        var result = await host.RunSampleAsync("cancel");

        Assert.Equal(ActionTools.CancelOrder, Assert.Single(host.Approvals.Requests).ToolName);
        Assert.Equal("cancelled", (await host.Repository.GetOrderDetailsAsync(1004))!.Status);
        Assert.Contains("cancelled", result.Reply);
    }

    [Fact]
    public async Task Refund_outside_the_policy_window_is_declined_without_asking_a_human()
    {
        await using var host = await TestHost.StartAsync();

        var result = await host.RunSampleAsync("policy");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Empty(host.Approvals.Requests);
        Assert.Empty(result.Actions);
        Assert.Contains("30-day", result.Reply);
    }

    [Fact]
    public async Task Every_agent_call_is_metered_and_the_run_is_persisted()
    {
        await using var host = await TestHost.StartAsync();

        var result = await host.RunSampleAsync("damaged");

        var llmCalls = result.Events.Where(e => e.Kind == RunEventKind.LlmCall).ToList();
        Assert.All(Enum.GetValues<AgentRole>(), role => Assert.Contains(llmCalls, e => e.Agent == role));
        Assert.All(llmCalls, e => Assert.True(e.InputTokens > 0 && e.OutputTokens > 0 && e.CostUsd > 0));
        Assert.Equal(llmCalls.Sum(e => e.CostUsd), result.Usage.CostUsd);

        var stored = await host.Services.GetRequiredService<IRunStore>().GetAsync(result.RunId);
        Assert.NotNull(stored);
        Assert.Equal(result.Events.Count, stored.Events.Count);
        Assert.Equal(RunStatus.Completed, stored.Summary.Status);
        Assert.Equal(1, stored.Summary.Approvals);
    }

    [Fact]
    public async Task Budget_guard_stops_the_run_before_overspending()
    {
        await using var host = await TestHost.StartAsync();

        var result = await host.RunSampleAsync("damaged", budgetUsd: 0.002m);

        Assert.Equal(RunStatus.BudgetExceeded, result.Status);
        Assert.Contains(result.Events, e => e.Kind == RunEventKind.BudgetExceeded);
        Assert.Null(result.Reply);
        Assert.Empty(host.Approvals.Requests);
    }
}
