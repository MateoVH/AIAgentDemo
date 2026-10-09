using System.ClientModel;
using System.Collections.Concurrent;
using AIAgentDemo.Core.Agents;
using AIAgentDemo.Core.Providers.Demo;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace AIAgentDemo.Core.Providers;

/// <summary>Builds the raw <see cref="IChatClient"/> each agent talks to. Agents never know the provider.</summary>
public interface IChatClientProvider
{
    AiProviderKind Kind { get; }

    bool IsSimulated { get; }

    AgentProfile GetProfile(AgentRole role);

    IChatClient GetChatClient(AgentRole role);
}

public sealed class ChatClientProvider : IChatClientProvider, IDisposable
{
    private readonly AiOptions _options;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<AnthropicClient> _anthropic;

    public ChatClientProvider(IOptions<AiOptions> options, ILogger<ChatClientProvider> logger)
    {
        _options = options.Value;
        Kind = Resolve(_options);
        _anthropic = new Lazy<AnthropicClient>(CreateAnthropicClient);

        if (Kind == AiProviderKind.Demo)
        {
            logger.LogInformation("AI provider: Demo (simulated model, no API key needed). Set AI:Provider and a key to use a real LLM.");
        }
        else
        {
            logger.LogInformation("AI provider: {Provider}, default model {Model}", Kind, Settings.Model);
        }
    }

    public AiProviderKind Kind { get; }

    public bool IsSimulated => Kind == AiProviderKind.Demo;

    private ProviderSettings Settings => Kind switch
    {
        AiProviderKind.Anthropic => _options.Anthropic,
        AiProviderKind.OpenAI => _options.OpenAI,
        AiProviderKind.AzureOpenAI => _options.AzureOpenAI,
        AiProviderKind.Ollama => _options.Ollama,
        _ => new ProviderSettings { Model = _options.Demo.Model },
    };

    public AgentProfile GetProfile(AgentRole role)
    {
        var defaults = AgentSettings.DefaultsFor(role);
        var overrides = _options.Agents.GetValueOrDefault(role.ToString());
        var model = !IsSimulated && !string.IsNullOrWhiteSpace(overrides?.Model) ? overrides.Model : Settings.Model;
        var effort = overrides?.ReasoningEffort ?? defaults.ReasoningEffort;
        var reasoning = Settings.SendReasoningEffort && effort is { } level ? new ReasoningOptions { Effort = level } : null;
        return new AgentProfile(role, model, overrides?.MaxOutputTokens ?? defaults.MaxOutputTokens!.Value, reasoning);
    }

    public IChatClient GetChatClient(AgentRole role) =>
        IsSimulated
            ? new DemoChatClient(role, _options.Demo)
            : _clients.GetOrAdd(GetProfile(role).Model, CreateClient);

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        if (_anthropic.IsValueCreated)
        {
            _anthropic.Value.Dispose();
        }
    }

    internal static AiProviderKind Resolve(AiOptions options)
    {
        if (Enum.TryParse<AiProviderKind>(options.Provider, ignoreCase: true, out var kind) && kind != AiProviderKind.Auto)
        {
            return kind;
        }

        if (HasKey(options.Anthropic.ApiKey, "ANTHROPIC_API_KEY"))
        {
            return AiProviderKind.Anthropic;
        }

        if (HasKey(options.OpenAI.ApiKey, "OPENAI_API_KEY"))
        {
            return AiProviderKind.OpenAI;
        }

        if (!string.IsNullOrWhiteSpace(options.AzureOpenAI.Endpoint) && !string.IsNullOrWhiteSpace(options.AzureOpenAI.ApiKey))
        {
            return AiProviderKind.AzureOpenAI;
        }

        return AiProviderKind.Demo;
    }

    private IChatClient CreateClient(string model) => Kind switch
    {
        // Official Anthropic SDK; its IChatClient maps ChatOptions.Reasoning to adaptive thinking + effort.
        AiProviderKind.Anthropic => _anthropic.Value.AsIChatClient(model),

        AiProviderKind.OpenAI => new OpenAIClient(
                new ApiKeyCredential(_options.OpenAI.ApiKey is { Length: > 0 } key ? key : RequiredEnv("OPENAI_API_KEY")),
                new OpenAIClientOptions { Endpoint = ToUri(_options.OpenAI.Endpoint) })
            .GetChatClient(model)
            .AsIChatClient(),

        // Azure OpenAI v1 API: the standard OpenAI client pointed at {resource}/openai/v1/.
        AiProviderKind.AzureOpenAI => new OpenAIClient(
                new ApiKeyCredential(Required(_options.AzureOpenAI.ApiKey, "AI:AzureOpenAI:ApiKey")),
                new OpenAIClientOptions { Endpoint = AzureV1Endpoint(Required(_options.AzureOpenAI.Endpoint, "AI:AzureOpenAI:Endpoint")) })
            .GetChatClient(model)
            .AsIChatClient(),

        AiProviderKind.Ollama => new OpenAIClient(
                new ApiKeyCredential("ollama"),
                new OpenAIClientOptions { Endpoint = ToUri(_options.Ollama.Endpoint) ?? new Uri("http://localhost:11434/v1") })
            .GetChatClient(model)
            .AsIChatClient(),

        _ => throw new InvalidOperationException($"Provider {Kind} does not use a remote client."),
    };

    private AnthropicClient CreateAnthropicClient() =>
        string.IsNullOrWhiteSpace(_options.Anthropic.ApiKey)
            ? new AnthropicClient() // ANTHROPIC_API_KEY or an `ant auth login` profile
            : new AnthropicClient { ApiKey = _options.Anthropic.ApiKey };

    private static bool HasKey(string? configured, string environmentVariable) =>
        !string.IsNullOrWhiteSpace(configured) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable));

    private static string RequiredEnv(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"No API key configured. Set {name} or the matching AI:* setting.");

    private static string Required(string? value, string setting) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Missing configuration value '{setting}'.") : value;

    private static Uri? ToUri(string? value) => string.IsNullOrWhiteSpace(value) ? null : new Uri(value);

    private static Uri AzureV1Endpoint(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');
        return new Uri(trimmed.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase) ? trimmed + "/" : trimmed + "/openai/v1/");
    }
}
