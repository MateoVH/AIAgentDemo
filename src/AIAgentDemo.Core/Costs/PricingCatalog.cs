using Microsoft.Extensions.Options;

namespace AIAgentDemo.Core.Costs;

public sealed class ModelPrice
{
    /// <summary>Model id or prefix ("gpt-5-mini" also prices "gpt-5-mini-2025-08-07").</summary>
    public string Model { get; set; } = "";

    public decimal InputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }

    /// <summary>Price for cache-hit input tokens; defaults to the regular input price.</summary>
    public decimal? CachedInputPerMillion { get; set; }
}

public sealed class PricingOptions
{
    public List<ModelPrice> Models { get; set; } = [];
}

public readonly record struct CostBreakdown(decimal TotalUsd, bool IsPriced);

/// <summary>USD per 1M tokens, from configuration. Longest-prefix match on the model id.</summary>
public sealed class PricingCatalog(IOptions<PricingOptions> options)
{
    private const decimal Million = 1_000_000m;

    private readonly ModelPrice[] _prices = options.Value.Models
        .Where(m => !string.IsNullOrWhiteSpace(m.Model))
        .OrderByDescending(m => m.Model.Length)
        .ToArray();

    public ModelPrice? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return _prices.FirstOrDefault(p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase))
            ?? _prices.FirstOrDefault(p => model.StartsWith(p.Model, StringComparison.OrdinalIgnoreCase));
    }

    public CostBreakdown Calculate(string? model, long inputTokens, long outputTokens, long cachedInputTokens = 0)
    {
        var price = Find(model);
        if (price is null)
        {
            return new CostBreakdown(0m, IsPriced: false);
        }

        var cached = Math.Clamp(cachedInputTokens, 0, inputTokens);
        var cost = (inputTokens - cached) * price.InputPerMillion / Million
                   + cached * (price.CachedInputPerMillion ?? price.InputPerMillion) / Million
                   + outputTokens * price.OutputPerMillion / Million;
        return new CostBreakdown(decimal.Round(cost, 6), IsPriced: true);
    }

    public decimal EstimateInputCost(string? model, long inputTokens) =>
        Find(model) is { } price ? inputTokens * price.InputPerMillion / Million : 0m;
}
