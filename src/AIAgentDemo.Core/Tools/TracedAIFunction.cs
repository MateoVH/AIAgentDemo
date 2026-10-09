using System.Diagnostics;
using System.Text.Json;
using AIAgentDemo.Core.Agents;
using AIAgentDemo.Core.Orchestration;
using AIAgentDemo.Core.Tracing;
using Microsoft.Extensions.AI;

namespace AIAgentDemo.Core.Tools;

/// <summary>
/// Decorates a tool so every invocation is logged (arguments, result, latency) in the run trace.
/// Data-tool results are also kept as evidence for the agents further down the pipeline.
/// </summary>
internal sealed class TracedAIFunction(AIFunction innerFunction, AgentRole agent, RunContext run, bool collectEvidence)
    : DelegatingAIFunction(innerFunction)
{
    private const int MaxDetailLength = 6_000;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var argumentsJson = JsonSerializer.SerializeToElement<IDictionary<string, object?>>(arguments, AgentJson.Options);
        run.Emit(RunEventKind.ToolCall, agent, Name, argumentsJson.GetRawText());

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            stopwatch.Stop();

            var resultJson = result is JsonElement element ? element : JsonSerializer.SerializeToElement(result, AgentJson.Options);
            run.Emit(RunEventKind.ToolResult, agent, Name, Truncate(resultJson.GetRawText()), durationMs: stopwatch.Elapsed.TotalMilliseconds);
            if (collectEvidence)
            {
                run.AddEvidence(new ToolEvidence(Name, argumentsJson, resultJson));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Emit(RunEventKind.ToolError, agent, Name, ex.Message, durationMs: stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxDetailLength ? text : string.Concat(text.AsSpan(0, MaxDetailLength), "…");
}
