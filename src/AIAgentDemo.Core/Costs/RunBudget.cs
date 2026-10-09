using AIAgentDemo.Core.Agents;

namespace AIAgentDemo.Core.Costs;

public sealed class BudgetOptions
{
    /// <summary>Hard ceiling per support run, in USD. The run stops before a call that would cross it.</summary>
    public decimal MaxCostPerRunUsd { get; set; } = 0.50m;

    /// <summary>Hard ceiling on input + output tokens per run.</summary>
    public long MaxTokensPerRun { get; set; } = 200_000;
}

public sealed class BudgetExceededException(string message, decimal spentUsd, decimal limitUsd) : Exception(message)
{
    public decimal SpentUsd { get; } = spentUsd;

    public decimal LimitUsd { get; } = limitUsd;
}

public sealed record AgentUsage(AgentRole Agent, string Model, int Calls, long InputTokens, long OutputTokens, decimal CostUsd);

public sealed record UsageSummary(
    IReadOnlyList<AgentUsage> ByAgent,
    int Calls,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    decimal BudgetUsd,
    bool HasUnpricedCalls)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// Token and cost accounting for one run. Shared by every agent of the run through
/// <see cref="UsageTrackingChatClient"/>, which checks it before and after each LLM call.
/// </summary>
public sealed class RunBudget(decimal maxCostUsd, long maxTokens)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<AgentRole, AgentUsage> _byAgent = [];
    private decimal _spent;
    private long _input;
    private long _output;
    private int _calls;
    private bool _unpriced;

    public decimal MaxCostUsd => maxCostUsd;

    public long MaxTokens => maxTokens;

    public decimal SpentUsd
    {
        get
        {
            lock (_gate)
            {
                return _spent;
            }
        }
    }

    /// <summary>Pre-flight check: refuse a call whose estimated input alone would cross a limit.</summary>
    public void EnsureCanSpend(decimal estimatedCostUsd, long estimatedTokens)
    {
        lock (_gate)
        {
            if (_spent + estimatedCostUsd > maxCostUsd)
            {
                throw new BudgetExceededException(
                    $"Budget guard: the next call (~{Money.Cost(estimatedCostUsd)} input) would exceed the {Money.Budget(maxCostUsd)} run budget (spent {Money.Cost(_spent)}).",
                    _spent,
                    maxCostUsd);
            }

            if (_input + _output + estimatedTokens > maxTokens)
            {
                throw new BudgetExceededException(
                    $"Budget guard: the next call (~{Tokens(estimatedTokens)} tokens) would exceed the {Tokens(maxTokens)}-token run limit.",
                    _spent,
                    maxCostUsd);
            }
        }
    }

    public void Record(AgentRole agent, string model, long inputTokens, long outputTokens, CostBreakdown cost)
    {
        lock (_gate)
        {
            _spent += cost.TotalUsd;
            _input += inputTokens;
            _output += outputTokens;
            _calls++;
            _unpriced |= !cost.IsPriced;

            _byAgent[agent] = _byAgent.TryGetValue(agent, out var current)
                ? current with
                {
                    Calls = current.Calls + 1,
                    InputTokens = current.InputTokens + inputTokens,
                    OutputTokens = current.OutputTokens + outputTokens,
                    CostUsd = current.CostUsd + cost.TotalUsd,
                }
                : new AgentUsage(agent, model, 1, inputTokens, outputTokens, cost.TotalUsd);
        }
    }

    /// <summary>Post-flight check: stop the run as soon as the recorded spend crosses a limit.</summary>
    public void ThrowIfExceeded()
    {
        lock (_gate)
        {
            if (_spent > maxCostUsd)
            {
                throw new BudgetExceededException(
                    $"Budget guard: spent {Money.Cost(_spent)} of a {Money.Budget(maxCostUsd)} run budget; the run was stopped.",
                    _spent,
                    maxCostUsd);
            }

            if (_input + _output > maxTokens)
            {
                throw new BudgetExceededException(
                    $"Budget guard: used {Tokens(_input + _output)} tokens of a {Tokens(maxTokens)}-token limit; the run was stopped.",
                    _spent,
                    maxCostUsd);
            }
        }
    }

    private static string Tokens(long count) => count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public UsageSummary Snapshot()
    {
        lock (_gate)
        {
            return new UsageSummary(
                _byAgent.Values.OrderBy(u => u.Agent).ToList(),
                _calls,
                _input,
                _output,
                _spent,
                maxCostUsd,
                _unpriced);
        }
    }
}
