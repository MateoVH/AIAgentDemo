using AIAgentDemo.Core.Agents;
using AIAgentDemo.Core.Costs;
using Microsoft.Extensions.Options;

namespace AIAgentDemo.Tests;

public class CostTests
{
    private static readonly PricingCatalog Catalog = new(Options.Create(new PricingOptions
    {
        Models =
        [
            new ModelPrice { Model = "claude-opus-5-5", InputPerMillion = 4m, OutputPerMillion = 20m },
            new ModelPrice { Model = "gpt-5", InputPerMillion = 1.25m, OutputPerMillion = 10m },
            new ModelPrice { Model = "gpt-5-mini", InputPerMillion = 0.25m, OutputPerMillion = 2m, CachedInputPerMillion = 0.025m },
        ],
    }));

    [Fact]
    public void Prices_input_and_output_per_million_tokens()
    {
        var cost = Catalog.Calculate("claude-opus-5-5", inputTokens: 10_000, outputTokens: 2_000);

        Assert.True(cost.IsPriced);
        Assert.Equal(0.08m, cost.TotalUsd); // 10k * $4/M + 2k * $20/M
    }

    [Fact]
    public void Longest_prefix_wins_for_dated_model_ids()
    {
        Assert.Equal("gpt-5-mini", Catalog.Find("gpt-5-mini-2025-08-07")?.Model);
        Assert.Equal("gpt-5", Catalog.Find("gpt-5-2025-08-07")?.Model);
    }

    [Fact]
    public void Cached_input_uses_the_cached_price()
    {
        var cost = Catalog.Calculate("gpt-5-mini", inputTokens: 1_000_000, outputTokens: 0, cachedInputTokens: 800_000);

        Assert.Equal(0.07m, cost.TotalUsd); // 200k * $0.25/M + 800k * $0.025/M
    }

    [Fact]
    public void Unknown_models_are_flagged_instead_of_guessed()
    {
        var cost = Catalog.Calculate("some-new-model", 1_000, 1_000);

        Assert.False(cost.IsPriced);
        Assert.Equal(0m, cost.TotalUsd);
    }

    [Theory]
    [InlineData(0.5, "$0.50")]
    [InlineData(0.002, "$0.002")]
    [InlineData(12, "$12.00")]
    public void Budgets_are_shown_without_noise(double amount, string expected) =>
        Assert.Equal(expected, AIAgentDemo.Core.Money.Budget((decimal)amount));

    [Theory]
    [InlineData(0.00261, "$0.00261")]
    [InlineData(0.0309, "$0.0309")]
    [InlineData(1.5, "$1.50")]
    public void Costs_keep_enough_decimals_to_be_meaningful(double amount, string expected) =>
        Assert.Equal(expected, AIAgentDemo.Core.Money.Cost((decimal)amount));

    [Fact]
    public void Budget_refuses_a_call_that_would_cross_the_limit()
    {
        var budget = new RunBudget(maxCostUsd: 0.01m, maxTokens: 100_000);
        budget.Record(AgentRole.Classifier, "m", 1_000, 100, new CostBreakdown(0.009m, true));

        Assert.Throws<BudgetExceededException>(() => budget.EnsureCanSpend(estimatedCostUsd: 0.002m, estimatedTokens: 500));
    }

    [Fact]
    public void Budget_stops_the_run_once_spend_crosses_the_limit()
    {
        var budget = new RunBudget(maxCostUsd: 0.01m, maxTokens: 100_000);
        budget.Record(AgentRole.Writer, "m", 1_000, 1_000, new CostBreakdown(0.02m, true));

        var exception = Assert.Throws<BudgetExceededException>(budget.ThrowIfExceeded);
        Assert.Equal(0.02m, exception.SpentUsd);
    }

    [Fact]
    public void Budget_aggregates_usage_per_agent()
    {
        var budget = new RunBudget(1m, 1_000_000);
        budget.Record(AgentRole.DataAnalyst, "m", 100, 10, new CostBreakdown(0.001m, true));
        budget.Record(AgentRole.DataAnalyst, "m", 200, 20, new CostBreakdown(0.002m, true));
        budget.Record(AgentRole.Writer, "m", 50, 50, new CostBreakdown(0.0005m, true));

        var snapshot = budget.Snapshot();
        var analyst = Assert.Single(snapshot.ByAgent, u => u.Agent == AgentRole.DataAnalyst);
        Assert.Equal(2, analyst.Calls);
        Assert.Equal(300, analyst.InputTokens);
        Assert.Equal(0.0035m, snapshot.CostUsd);
        Assert.Equal(3, snapshot.Calls);
    }
}
