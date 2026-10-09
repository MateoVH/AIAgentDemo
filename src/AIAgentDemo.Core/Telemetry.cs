using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AIAgentDemo.Core;

/// <summary>OpenTelemetry sources. Agents and chat clients also emit GenAI spans under this source name.</summary>
public static class Telemetry
{
    public const string SourceName = "AIAgentDemo";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<double> CostUsd =
        Meter.CreateCounter<double>("agentdesk.llm.cost", unit: "USD", description: "Estimated LLM spend per agent.");

    public static readonly Counter<long> Tokens =
        Meter.CreateCounter<long>("agentdesk.llm.tokens", unit: "{token}", description: "LLM tokens per agent and direction.");

    public static readonly Counter<long> Runs =
        Meter.CreateCounter<long>("agentdesk.runs", unit: "{run}", description: "Support runs by final status.");
}
