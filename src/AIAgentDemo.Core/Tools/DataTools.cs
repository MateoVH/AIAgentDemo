using System.ComponentModel;
using AIAgentDemo.Core.Data;
using Microsoft.Extensions.AI;

namespace AIAgentDemo.Core.Tools;

/// <summary>Read-only tools for the Data Analyst agent. None of them can modify the store.</summary>
internal static class DataTools
{
    public static IReadOnlyList<AIFunction> Create(StoreRepository repository) =>
    [
        AIFunctionFactory.Create(
            async ([Description("Customer email address")] string email, CancellationToken ct) =>
                (object?)await repository.GetCustomerProfileAsync(email, ct)
                ?? new { found = false, message = $"No customer found with email '{email}'." },
            "get_customer_profile",
            "Looks up a customer by email: name, country, preferred language, loyalty tier, order count and lifetime value.",
            AgentJson.Options),

        AIFunctionFactory.Create(
            async ([Description("Numeric order id, e.g. 1001")] int orderId, CancellationToken ct) =>
                (object?)await repository.GetOrderDetailsAsync(orderId, ct)
                ?? new { found = false, message = $"Order {orderId} does not exist." },
            "get_order_details",
            "Gets one order: owner, status, total, refunded amount, dates, carrier, tracking number, items, and days since order/shipment/delivery.",
            AgentJson.Options),

        AIFunctionFactory.Create(
            async (
                [Description("Customer email address")] string email,
                [Description("Maximum number of orders to return (1-20)")] int limit,
                CancellationToken ct) => await repository.ListCustomerOrdersAsync(email, limit, ct),
            "list_customer_orders",
            "Lists a customer's most recent orders (newest first) with status, total and items. Use when the order number is unknown.",
            AgentJson.Options),

        AIFunctionFactory.Create(
            async ([Description("Product name, category, SKU or keywords (English or Spanish)")] string query, CancellationToken ct) =>
                await repository.SearchProductsAsync(query, ct),
            "search_products",
            "Searches the catalog and returns matching products with price, stock and description.",
            AgentJson.Options),

        AIFunctionFactory.Create(
            async ([Description("A single read-only SQLite SELECT statement")] string sql, CancellationToken ct) =>
                await repository.RunReadOnlyQueryAsync(sql, ct),
            "run_sql_query",
            $"Runs one read-only SELECT against the store database (max {StoreRepository.MaxSqlRows} rows). " +
            "Only use it when the other tools cannot answer. Writes, PRAGMA and multiple statements are rejected.",
            AgentJson.Options),
    ];
}
