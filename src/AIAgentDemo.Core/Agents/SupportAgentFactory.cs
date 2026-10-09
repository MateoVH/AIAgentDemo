using AIAgentDemo.Core.Costs;
using AIAgentDemo.Core.Data;
using AIAgentDemo.Core.Orchestration;
using AIAgentDemo.Core.Providers;
using AIAgentDemo.Core.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgentDemo.Core.Agents;

/// <summary>
/// Creates the Microsoft Agent Framework agents for a run. The chat pipeline of every agent is:
/// <code>
/// ChatClientAgent (+ approval binding)  →  FunctionInvokingChatClient (tool loop)
///   →  UsageTrackingChatClient (budget + tokens + cost per call)  →  OpenTelemetry  →  provider
/// </code>
/// Agents are cheap objects, so they are created per run with tools bound to that run.
/// </summary>
public sealed class SupportAgentFactory(
    IChatClientProvider provider,
    PricingCatalog pricing,
    StoreRepository repository,
    StoreDatabase database,
    TimeProvider time,
    IOptions<AiOptions> aiOptions,
    IOptions<ApprovalOptions> approvalOptions,
    ILoggerFactory loggerFactory)
{
    private readonly IReadOnlySet<string> _approvalRequired = approvalOptions.Value.EffectiveRequiredFor;

    public AIAgent Create(AgentRole role, RunContext run)
    {
        var profile = provider.GetProfile(role);

        var chatClient = provider.GetChatClient(role)
            .AsBuilder()
            .UseFunctionInvocation(loggerFactory, invoker =>
            {
                invoker.MaximumIterationsPerRequest = aiOptions.Value.MaxToolIterations;
                invoker.IncludeDetailedErrors = false;
            })
            .Use(inner => new UsageTrackingChatClient(inner, role, run, pricing, profile.Model))
            .UseOpenTelemetry(loggerFactory, Telemetry.SourceName)
            .Build();

        var agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = NameOf(role),
                Description = DescriptionOf(role),
                ChatOptions = new ChatOptions
                {
                    Instructions = InstructionsOf(role),
                    MaxOutputTokens = profile.MaxOutputTokens,
                    Reasoning = profile.Reasoning,
                    Tools = CreateTools(role, run),
                },
            },
            loggerFactory);

        return agent.AsBuilder()
            .UseOpenTelemetry(Telemetry.SourceName)
            .Build();
    }

    public static string NameOf(AgentRole role) => role switch
    {
        AgentRole.Classifier => "triage_classifier",
        AgentRole.DataAnalyst => "sql_data_analyst",
        AgentRole.Supervisor => "supervisor",
        _ => "response_writer",
    };

    private static string DescriptionOf(AgentRole role) => role switch
    {
        AgentRole.Classifier => "Classifies intent, language, urgency and entities of a customer message.",
        AgentRole.DataAnalyst => "Investigates the case with read-only SQL tools.",
        AgentRole.Supervisor => "Applies business policy and proposes actions that a human approves.",
        _ => "Drafts the customer reply in the customer's language.",
    };

    private string InstructionsOf(AgentRole role) => role switch
    {
        AgentRole.Classifier => AgentInstructions.Classifier,
        AgentRole.DataAnalyst => AgentInstructions.DataAnalyst,
        AgentRole.Supervisor => AgentInstructions.Supervisor(_approvalRequired.Order()),
        _ => AgentInstructions.Writer,
    };

    private List<AITool>? CreateTools(AgentRole role, RunContext run) => role switch
    {
        AgentRole.DataAnalyst => DataTools.Create(repository)
            .Select(AITool (tool) => new TracedAIFunction(tool, role, run, collectEvidence: true))
            .ToList(),

        AgentRole.Supervisor => new ActionTools(database, run, time, _approvalRequired)
            .CreateFunctions()
            .Select(AITool (tool) =>
            {
                var traced = new TracedAIFunction(tool, role, run, collectEvidence: false);
                // The function-invocation loop pauses on these and surfaces a ToolApprovalRequestContent.
                return _approvalRequired.Contains(tool.Name) ? new ApprovalRequiredAIFunction(traced) : traced;
            })
            .ToList(),

        _ => null,
    };
}
