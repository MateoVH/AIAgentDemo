using System.Text.Json;

namespace AIAgentDemo.Core.Orchestration;

/// <summary>Case data handed from one agent to the next, serialized as JSON in the prompt.</summary>
public sealed record CaseContext
{
    public string OperatorLanguage { get; init; } = "en";

    public string? CustomerLanguage { get; init; }

    public string CustomerEmail { get; init; } = "";

    public TriageResult? Triage { get; init; }

    public string? Facts { get; init; }

    public IReadOnlyList<ToolEvidence>? Evidence { get; init; }

    public IReadOnlyList<ActionOutcome>? Actions { get; init; }

    public string? SupervisorNotes { get; init; }
}

/// <summary>
/// Prompt format shared by all agents: a &lt;case_context&gt; JSON block, the fenced customer
/// message, and the task. Fencing keeps untrusted customer text clearly separated from data.
/// </summary>
public static class CasePrompts
{
    private const string ContextOpen = "<case_context>";
    private const string ContextClose = "</case_context>";
    private const string MessageOpen = "<customer_message>";
    private const string MessageClose = "</customer_message>";

    public static string Build(CaseContext context, string customerMessage, string task) =>
        $"""
        {ContextOpen}
        {AgentJson.Serialize(context)}
        {ContextClose}
        {MessageOpen}
        {Fence(customerMessage)}
        {MessageClose}
        {task}
        """;

    public static bool TryParse(string prompt, out CaseContext context, out string customerMessage)
    {
        context = new CaseContext();
        customerMessage = Between(prompt, MessageOpen, MessageClose) ?? string.Empty;
        var json = Between(prompt, ContextOpen, ContextClose);
        if (json is null)
        {
            return false;
        }

        try
        {
            context = JsonSerializer.Deserialize<CaseContext>(json, AgentJson.Options) ?? new CaseContext();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // A customer cannot close the fence early and smuggle text outside of it.
    private static string Fence(string message) =>
        message
            .Replace(MessageClose, "[/customer_message]", StringComparison.OrdinalIgnoreCase)
            .Replace(ContextOpen, "[case_context]", StringComparison.OrdinalIgnoreCase)
            .Trim();

    private static string? Between(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end].Trim();
    }
}

/// <summary>Lenient JSON extraction for models that wrap structured output in prose or code fences.</summary>
public static class StructuredOutput
{
    public static T? TryParse<T>(string? text)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text[start..(end + 1)], AgentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
