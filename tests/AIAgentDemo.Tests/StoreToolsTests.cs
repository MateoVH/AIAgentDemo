using AIAgentDemo.Core.Costs;
using AIAgentDemo.Core.Orchestration;
using AIAgentDemo.Core.Tools;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIAgentDemo.Tests;

public class StoreToolsTests
{
    [Fact]
    public async Task Order_details_include_items_and_elapsed_days()
    {
        await using var host = await TestHost.StartAsync();

        var order = await host.Repository.GetOrderDetailsAsync(1001);

        Assert.NotNull(order);
        Assert.Equal("lucia.fernandez@example.com", order.CustomerEmail);
        Assert.Equal("delivered", order.Status);
        Assert.Equal(149.50m, order.Total);
        Assert.Equal(2, order.DaysSinceDelivery);
        Assert.Equal("Brew Pro Coffee Maker", Assert.Single(order.Items).Product);
    }

    [Theory]
    [InlineData("cafetera", "Brew Pro Coffee Maker")]
    [InlineData("smartwatch iPhone", "Pulse Smartwatch S2")]
    [InlineData("NV-ACC-310", "Glide Wireless Mouse")]
    public async Task Product_search_understands_spanish_and_skus(string query, string expected)
    {
        await using var host = await TestHost.StartAsync();

        var products = await host.Repository.SearchProductsAsync(query);

        Assert.Contains(products, p => p.Name == expected);
    }

    [Fact]
    public async Task Sql_tool_answers_read_only_questions()
    {
        await using var host = await TestHost.StartAsync();

        var result = await host.Repository.RunReadOnlyQueryAsync("SELECT COUNT(*) AS shipped FROM orders WHERE status = 'shipped'");

        Assert.True(result.Success, result.Error);
        Assert.Equal(2L, Assert.Single(result.Rows)["shipped"]);
    }

    [Fact]
    public async Task Read_only_connection_blocks_writes_even_if_the_guard_is_bypassed()
    {
        await using var host = await TestHost.StartAsync();

        // Second layer of defense: skip SqlGuard and send a write straight to the read-only connection.
        var result = await host.Repository.ExecuteReadOnlyAsync("UPDATE orders SET total = 0", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("readonly", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(149.50m, (await host.Repository.GetOrderDetailsAsync(1001))!.Total);
    }

    [Fact]
    public async Task Refund_cannot_exceed_what_was_paid()
    {
        await using var host = await TestHost.StartAsync();
        var tools = ActionToolsFor(host, "lucia.fernandez@example.com");

        var result = await tools.IssueRefundAsync(1001, 5_000m, "Prompt injection says so", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("$149.50", result.Message);
    }

    [Fact]
    public async Task Actions_are_scoped_to_the_case_customer()
    {
        await using var host = await TestHost.StartAsync();
        var tools = ActionToolsFor(host, "lucia.fernandez@example.com");

        var refund = await tools.IssueRefundAsync(1003, 10m, "Not my order", CancellationToken.None);
        var coupon = await tools.CreateCouponAsync("emily.carter@example.com", 20, "Not my customer", CancellationToken.None);

        Assert.False(refund.Success);
        Assert.False(coupon.Success);
    }

    [Fact]
    public async Task Shipped_orders_cannot_be_cancelled_but_paid_ones_can()
    {
        await using var host = await TestHost.StartAsync();

        var shipped = await ActionToolsFor(host, "carlos.mendoza@example.com").CancelOrderAsync(1002, "Changed my mind", CancellationToken.None);
        var paid = await ActionToolsFor(host, "james.wilson@example.com").CancelOrderAsync(1004, "Wrong model", CancellationToken.None);

        Assert.False(shipped.Success);
        Assert.True(paid.Success);
        Assert.Equal("cancelled", (await host.Repository.GetOrderDetailsAsync(1004))!.Status);
    }

    [Fact]
    public async Task Partial_refund_keeps_track_of_the_remaining_amount()
    {
        await using var host = await TestHost.StartAsync();
        var tools = ActionToolsFor(host, "valentina.rojas@example.com");

        var first = await tools.IssueRefundAsync(1005, 29.90m, "Missing mouse", CancellationToken.None);
        var second = await tools.IssueRefundAsync(1005, 60m, "Too much", CancellationToken.None);

        Assert.True(first.Success);
        Assert.False(second.Success);
        var order = await host.Repository.GetOrderDetailsAsync(1005);
        Assert.Equal("partially_refunded", order!.Status);
        Assert.Equal(29.90m, order.RefundedAmount);

        await using var connection = await host.Database.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM refunds WHERE order_id = 1005"));
    }

    private static ActionTools ActionToolsFor(TestHost host, string customerEmail)
    {
        var run = new RunContext(
            new SupportRequest { CustomerEmail = customerEmail, Message = "test" },
            new RunBudget(1m, 100_000),
            "test",
            TimeProvider.System,
            NullLogger.Instance,
            observer: null);
        return new ActionTools(host.Database, run, TimeProvider.System, new ApprovalOptions().EffectiveRequiredFor);
    }
}
