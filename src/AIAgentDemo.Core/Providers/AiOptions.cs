using AIAgentDemo.Core.Agents;
using Microsoft.Extensions.AI;

namespace AIAgentDemo.Core.Providers;

public enum AiProviderKind
{
    /// <summary>Anthropic if a Claude key is found, then OpenAI, then Azure OpenAI, otherwise Demo.</summary>
    Auto,

    /// <summary>Offline, deterministic simulated model. No API key needed.</summary>
    Demo,

    Anthropic,
    OpenAI,
    AzureOpenAI,

    /// <summary>Local models through Ollama's OpenAI-compatible endpoint.</summary>
    Ollama,
}

public sealed class AiOptions
{
    public string Provider { get; set; } = nameof(AiProviderKind.Auto);

    public ProviderSettings Anthropic { get; set; } = new() { Model = "claude-opus-5-5", SendReasoningEffort = true };

    public ProviderSettings OpenAI { get; set; } = new() { Model = "gpt-5-mini", SendReasoningEffort = true };

    /// <summary>Endpoint is the resource URL (https://name.openai.azure.com); Model is the deployment name.</summary>
    public ProviderSettings AzureOpenAI { get; set; } = new() { Model = "gpt-5-mini", SendReasoningEffort = true };

    public ProviderSettings Ollama { get; set; } = new() { Endpoint = "http://localhost:11434/v1", Model = "qwen3:8b" };

    public DemoSettings Demo { get; set; } = new();

    /// <summary>Per-agent overrides keyed by <see cref="AgentRole"/> name (model, output cap, reasoning effort).</summary>
    public Dictionary<string, AgentSettings> Agents { get; set; } = [];

    /// <summary>Upper bound of model round-trips per agent invocation (tool-calling loop guard).</summary>
    public int MaxToolIterations { get; set; } = 6;
}

public sealed class ProviderSettings
{
    public string? ApiKey { get; set; }

    public string? Endpoint { get; set; }

    public string Model { get; set; } = "";

    /// <summary>Send the per-agent reasoning effort. Disable for models that reject it.</summary>
    public bool SendReasoningEffort { get; set; }
}

public sealed class DemoSettings
{
    /// <summary>Simulated latency per model call, so the UI timeline can be followed.</summary>
    public int LatencyMs { get; set; } = 650;

    /// <summary>Name used for pricing the simulated usage.</summary>
    public string Model { get; set; } = "demo-simulated";
}

public sealed class AgentSettings
{
    public string? Model { get; set; }

    public int? MaxOutputTokens { get; set; }

    public ReasoningEffort? ReasoningEffort { get; set; }

    /// <summary>Simple tasks run at low effort; the policy decision gets a bit more thinking.</summary>
    internal static AgentSettings DefaultsFor(AgentRole role) => role switch
    {
        AgentRole.Classifier => new() { MaxOutputTokens = 2048, ReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort.Low },
        AgentRole.DataAnalyst => new() { MaxOutputTokens = 4096, ReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort.Low },
        AgentRole.Supervisor => new() { MaxOutputTokens = 4096, ReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort.Medium },
        _ => new() { MaxOutputTokens = 2048, ReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort.Low },
    };
}

/// <summary>Resolved model settings for one agent.</summary>
public sealed record AgentProfile(AgentRole Role, string Model, int MaxOutputTokens, ReasoningOptions? Reasoning);
