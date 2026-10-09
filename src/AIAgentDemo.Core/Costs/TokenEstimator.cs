using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AIAgentDemo.Core.Costs;

/// <summary>
/// Rough token estimate (~4 characters per token). Used for the pre-flight budget check,
/// for providers that do not report usage, and by the simulated demo model.
/// </summary>
public static class TokenEstimator
{
    private const int CharsPerToken = 4;
    private const int PerMessageOverhead = 4;

    public static long EstimateInput(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        long chars = options?.Instructions?.Length ?? 0;
        foreach (var tool in options?.Tools ?? [])
        {
            chars += tool.Name.Length + tool.Description.Length;
            if (tool is AIFunctionDeclaration function)
            {
                chars += function.JsonSchema.GetRawText().Length;
            }
        }

        long count = 0;
        foreach (var message in messages)
        {
            count++;
            chars += ContentLength(message.Contents);
        }

        return chars / CharsPerToken + count * PerMessageOverhead;
    }

    public static long EstimateOutput(IEnumerable<ChatMessage> messages) =>
        messages.Sum(m => ContentLength(m.Contents)) / CharsPerToken + 1;

    private static long ContentLength(IEnumerable<AIContent> contents)
    {
        long chars = 0;
        foreach (var content in contents)
        {
            chars += content switch
            {
                TextContent text => text.Text.Length,
                TextReasoningContent reasoning => reasoning.Text.Length,
                FunctionCallContent call => call.Name.Length + JsonLength(call.Arguments),
                FunctionResultContent result => JsonLength(result.Result),
                _ => 16,
            };
        }

        return chars;
    }

    private static int JsonLength(object? value) => value switch
    {
        null => 4,
        string s => s.Length,
        JsonElement element => element.GetRawText().Length,
        _ => JsonSerializer.Serialize(value, AgentJson.Options).Length,
    };
}
