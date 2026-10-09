using AIAgentDemo.Core.Agents;
using AIAgentDemo.Core.Orchestration;
using AIAgentDemo.Core.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AIAgentDemo.Tests;

public class ResilienceTests
{
    [Fact]
    public async Task Classifier_falls_back_to_plain_json_when_the_provider_rejects_json_schema()
    {
        await using var host = await TestHost.StartAsync(configure: services =>
            services.AddSingleton<IChatClientProvider>(sp =>
                new SchemaRejectingProvider(ActivatorUtilities.CreateInstance<ChatClientProvider>(sp))));

        var result = await host.RunSampleAsync("damaged");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(SupportIntent.DamagedItem, result.Triage!.Intent);
        Assert.Equal(1001, result.Triage.OrderId);
    }

    /// <summary>Simulates a model or endpoint that does not support JSON-schema output for the classifier.</summary>
    private sealed class SchemaRejectingProvider(IChatClientProvider inner) : IChatClientProvider
    {
        public AiProviderKind Kind => inner.Kind;

        public bool IsSimulated => inner.IsSimulated;

        public AgentProfile GetProfile(AgentRole role) => inner.GetProfile(role);

        public IChatClient GetChatClient(AgentRole role) =>
            role == AgentRole.Classifier ? new RejectJsonSchema(inner.GetChatClient(role)) : inner.GetChatClient(role);
    }

    private sealed class RejectJsonSchema(IChatClient innerClient) : DelegatingChatClient(innerClient)
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            options?.ResponseFormat is ChatResponseFormatJson { Schema: not null }
                ? throw new InvalidOperationException("400 Bad Request: response_format json_schema is not supported by this model.")
                : base.GetResponseAsync(messages, options, cancellationToken);
    }
}
