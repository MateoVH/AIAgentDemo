using System.Runtime.CompilerServices;
using System.Text.Json;
using AIAgentDemo.Core.Agents;
using AIAgentDemo.Core.Costs;
using AIAgentDemo.Core.Orchestration;
using Microsoft.Extensions.AI;

namespace AIAgentDemo.Core.Providers.Demo;

/// <summary>
/// Offline stand-in for an LLM, so the project runs (and its tests pass) without an API key.
/// It travels the exact same path as a real model — JSON structured output, tool calls executed by
/// the function-invocation loop, approval pauses, token metering — but its "reasoning" is a set of
/// deterministic rules in <see cref="DemoBrain"/>. Token counts are estimated from text length.
/// </summary>
public sealed class DemoChatClient(AgentRole role, DemoSettings settings) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        if (settings.LatencyMs > 0)
        {
            var jitter = Random.Shared.Next(-settings.LatencyMs / 3, settings.LatencyMs / 3 + 1);
            await Task.Delay(settings.LatencyMs + jitter, cancellationToken);
        }

        var conversation = DemoConversation.From(history);
        var reply = role switch
        {
            AgentRole.Classifier => DemoBrain.Classify(conversation),
            AgentRole.DataAnalyst => DemoBrain.Investigate(conversation),
            AgentRole.Supervisor => DemoBrain.Decide(conversation),
            _ => DemoBrain.Write(conversation),
        };

        var inputTokens = TokenEstimator.EstimateInput(history, options);
        var outputTokens = TokenEstimator.EstimateOutput([reply]);
        return new ChatResponse(reply)
        {
            ModelId = settings.Model,
            CreatedAt = DateTimeOffset.UtcNow,
            FinishReason = reply.Contents.OfType<FunctionCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = inputTokens + outputTokens,
            },
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("demo", null, settings.Model)
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }
}

/// <summary>A tool result seen in the conversation, with the arguments of the call that produced it.</summary>
internal sealed record DemoToolResult(string Tool, JsonElement Arguments, JsonElement? Json, string? Text)
{
    public bool NotFound => Json is { ValueKind: JsonValueKind.Object } json
        && json.TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.False;

    public T? As<T>()
        where T : class =>
        Json is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } json && !NotFound
            ? json.Deserialize<T>(AgentJson.Options)
            : null;
}

/// <summary>What the simulated model "reads": the case context, the customer message and tool results so far.</summary>
internal sealed record DemoConversation(
    CaseContext Context,
    string CustomerMessage,
    IReadOnlyList<DemoToolResult> ToolResults,
    string? AssistantText)
{
    public bool IsSpanish => Context.OperatorLanguage.StartsWith("es", StringComparison.OrdinalIgnoreCase);

    public DemoToolResult? Result(string tool) => ToolResults.LastOrDefault(r => r.Tool == tool);

    public static DemoConversation From(IReadOnlyList<ChatMessage> history)
    {
        var context = new CaseContext();
        var message = string.Empty;
        foreach (var candidate in history.Where(m => m.Role == ChatRole.User))
        {
            if (CasePrompts.TryParse(candidate.Text, out var parsed, out var customerMessage))
            {
                context = parsed;
                message = customerMessage;
                break;
            }
        }

        var calls = new Dictionary<string, FunctionCallContent>();
        foreach (var call in history.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
        {
            calls[call.CallId] = call;
        }

        var results = history
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Select(result =>
            {
                calls.TryGetValue(result.CallId, out var call);
                var arguments = JsonSerializer.SerializeToElement(call?.Arguments ?? new Dictionary<string, object?>(), AgentJson.Options);
                var (json, text) = Parse(result.Result);
                return new DemoToolResult(call?.Name ?? "unknown", arguments, json, text);
            })
            .ToList();

        // What "the model" said earlier in this conversation (e.g. the rationale that preceded a tool call).
        var assistantText = history
            .Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents)
            .OfType<TextContent>()
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t.Text))?.Text;

        return new DemoConversation(context, message, results, assistantText);
    }

    private static (JsonElement? Json, string? Text) Parse(object? value)
    {
        switch (value)
        {
            case null:
                return (null, null);
            case JsonElement element when element.ValueKind == JsonValueKind.String:
                return Parse(element.GetString());
            case JsonElement element:
                return (element, null);
            case string text:
                try
                {
                    using var document = JsonDocument.Parse(text);
                    return (document.RootElement.Clone(), null);
                }
                catch (JsonException)
                {
                    return (null, text);
                }

            default:
                return (JsonSerializer.SerializeToElement(value, AgentJson.Options), null);
        }
    }
}
