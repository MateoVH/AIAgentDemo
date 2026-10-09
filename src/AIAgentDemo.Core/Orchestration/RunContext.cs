using AIAgentDemo.Core.Agents;
using AIAgentDemo.Core.Costs;
using AIAgentDemo.Core.Tracing;
using Microsoft.Extensions.Logging;

namespace AIAgentDemo.Core.Orchestration;

/// <summary>
/// Mutable state of one support run, shared by the supervisor, the agents' middleware and the tools:
/// the event log, the budget, the evidence gathered and the actions taken.
/// </summary>
public sealed class RunContext
{
    private readonly Lock _gate = new();
    private readonly List<RunEvent> _events = [];
    private readonly List<ToolEvidence> _evidence = [];
    private readonly List<ActionOutcome> _actions = [];
    private readonly Action<RunEvent>? _observer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public RunContext(SupportRequest request, RunBudget budget, string provider, TimeProvider time, ILogger logger, Action<RunEvent>? observer)
    {
        Request = request;
        Budget = budget;
        Provider = provider;
        _time = time;
        _logger = logger;
        _observer = observer;
        StartedAt = time.GetUtcNow();
    }

    public string RunId => Request.RunId;

    public SupportRequest Request { get; }

    public RunBudget Budget { get; }

    public string Provider { get; }

    public DateTimeOffset StartedAt { get; }

    public IReadOnlyList<RunEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public IReadOnlyList<ToolEvidence> Evidence
    {
        get
        {
            lock (_gate)
            {
                return _evidence.ToArray();
            }
        }
    }

    public IReadOnlyList<ActionOutcome> Actions
    {
        get
        {
            lock (_gate)
            {
                return _actions.ToArray();
            }
        }
    }

    public RunEvent Emit(
        RunEventKind kind,
        AgentRole? agent = null,
        string? name = null,
        string? detail = null,
        long? inputTokens = null,
        long? outputTokens = null,
        decimal? costUsd = null,
        double? durationMs = null)
    {
        RunEvent runEvent;
        lock (_gate)
        {
            runEvent = new RunEvent
            {
                RunId = RunId,
                Sequence = _events.Count + 1,
                Timestamp = _time.GetUtcNow(),
                Kind = kind,
                Agent = agent,
                Name = name,
                Detail = detail,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CostUsd = costUsd,
                DurationMs = durationMs,
            };
            _events.Add(runEvent);
        }

        _logger.LogInformation(
            "[{RunId}] #{Sequence} {Kind} {Agent} {Name} tokens={InputTokens}/{OutputTokens} cost={CostUsd}",
            RunId, runEvent.Sequence, kind, agent, name, inputTokens, outputTokens, costUsd);

        try
        {
            _observer?.Invoke(runEvent);
        }
        catch (Exception ex)
        {
            // A broken observer (e.g. a closed browser tab) must never break the run.
            _logger.LogWarning(ex, "[{RunId}] Run observer threw", RunId);
        }

        return runEvent;
    }

    public void AddEvidence(ToolEvidence evidence)
    {
        lock (_gate)
        {
            _evidence.Add(evidence);
        }
    }

    public void RecordAction(ActionOutcome outcome)
    {
        lock (_gate)
        {
            _actions.Add(outcome);
        }
    }
}
