using System.Diagnostics;
using System.Runtime.CompilerServices;
using AIAgentDemo.Core.Agents;
using AIAgentDemo.Core.Orchestration;
using AIAgentDemo.Core.Tracing;
using Microsoft.Extensions.AI;

namespace AIAgentDemo.Core.Costs;

/// <summary>
/// Chat-client middleware that sits below the function-invocation loop, so it sees every single
/// round-trip to the model: it enforces the run budget before the call, then records tokens,
/// cost and latency as a <see cref="RunEventKind.LlmCall"/> event.
/// </summary>
internal sealed class UsageTrackingChatClient(
    IChatClient innerClient,
    AgentRole agent,
    RunContext run,
    PricingCatalog pricing,
    string defaultModel) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        var model = options?.ModelId ?? defaultModel;
        var estimatedInput = TokenEstimator.EstimateInput(history, options);
        run.Budget.EnsureCanSpend(pricing.EstimateInputCost(model, estimatedInput), estimatedInput);

        var stopwatch = Stopwatch.StartNew();
        var response = await base.GetResponseAsync(history, options, cancellationToken);
        stopwatch.Stop();

        Record(response.ModelId ?? model, response.Usage, estimatedInput, response.Messages, response.FinishReason, stopwatch.Elapsed);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        var model = options?.ModelId ?? defaultModel;
        var estimatedInput = TokenEstimator.EstimateInput(history, options);
        run.Budget.EnsureCanSpend(pricing.EstimateInputCost(model, estimatedInput), estimatedInput);

        var stopwatch = Stopwatch.StartNew();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in base.GetStreamingResponseAsync(history, options, cancellationToken))
        {
            updates.Add(update);
            yield return update;
        }

        stopwatch.Stop();
        var response = updates.ToChatResponse();
        Record(response.ModelId ?? model, response.Usage, estimatedInput, response.Messages, response.FinishReason, stopwatch.Elapsed);
    }

    private void Record(
        string model,
        UsageDetails? usage,
        long estimatedInput,
        IList<ChatMessage> output,
        ChatFinishReason? finishReason,
        TimeSpan elapsed)
    {
        var estimated = usage?.InputTokenCount is null || usage.OutputTokenCount is null;
        var input = usage?.InputTokenCount ?? estimatedInput;
        var outputTokens = usage?.OutputTokenCount ?? TokenEstimator.EstimateOutput(output);
        var cached = usage?.CachedInputTokenCount ?? 0;
        var cost = pricing.Calculate(model, input, outputTokens, cached);

        run.Budget.Record(agent, model, input, outputTokens, cost);
        run.Emit(
            RunEventKind.LlmCall,
            agent,
            name: model,
            detail: AgentJson.Serialize(new
            {
                finishReason = finishReason?.Value,
                cachedInputTokens = cached > 0 ? cached : (long?)null,
                reasoningTokens = usage?.ReasoningTokenCount,
                usageEstimated = estimated ? true : (bool?)null,
                priced = cost.IsPriced ? (bool?)null : false,
            }),
            inputTokens: input,
            outputTokens: outputTokens,
            costUsd: cost.TotalUsd,
            durationMs: elapsed.TotalMilliseconds);

        var tags = new TagList { { "agent", agent.ToString() }, { "model", model } };
        Telemetry.CostUsd.Add((double)cost.TotalUsd, tags);
        Telemetry.Tokens.Add(input, new TagList { { "agent", agent.ToString() }, { "model", model }, { "direction", "input" } });
        Telemetry.Tokens.Add(outputTokens, new TagList { { "agent", agent.ToString() }, { "model", model }, { "direction", "output" } });

        run.Budget.ThrowIfExceeded();
    }
}
