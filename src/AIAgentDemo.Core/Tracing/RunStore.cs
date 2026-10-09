using System.Globalization;
using AIAgentDemo.Core.Agents;
using AIAgentDemo.Core.Data;
using AIAgentDemo.Core.Orchestration;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AIAgentDemo.Core.Tracing;

public sealed record RunSummary
{
    public required string RunId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public double DurationMs { get; init; }
    public RunStatus Status { get; init; }
    public string Provider { get; init; } = "";
    public string CustomerEmail { get; init; } = "";
    public string Message { get; init; } = "";
    public SupportIntent? Intent { get; init; }
    public string? Language { get; init; }
    public string? Reply { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal CostUsd { get; init; }
    public decimal BudgetUsd { get; init; }
    public int LlmCalls { get; init; }
    public int ToolCalls { get; init; }
    public int Approvals { get; init; }
    public string? Error { get; init; }
}

public sealed record RunRecord(RunSummary Summary, IReadOnlyList<RunEvent> Events)
{
    public static RunRecord From(RunContext run, RunResult result) => new(
        new RunSummary
        {
            RunId = run.RunId,
            StartedAt = run.StartedAt,
            DurationMs = result.Duration.TotalMilliseconds,
            Status = result.Status,
            Provider = run.Provider,
            CustomerEmail = run.Request.CustomerEmail,
            Message = run.Request.Message,
            Intent = result.Triage?.Intent,
            Language = result.Triage?.Language,
            Reply = result.Reply,
            InputTokens = result.Usage.InputTokens,
            OutputTokens = result.Usage.OutputTokens,
            CostUsd = result.Usage.CostUsd,
            BudgetUsd = result.Usage.BudgetUsd,
            LlmCalls = result.Usage.Calls,
            ToolCalls = result.Events.Count(e => e.Kind == RunEventKind.ToolCall),
            Approvals = result.Events.Count(e => e.Kind == RunEventKind.ApprovalRequested),
            Error = result.Error,
        },
        result.Events);
}

/// <summary>Audit log of runs: every step with tokens, cost and latency.</summary>
public interface IRunStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(RunRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RunSummary>> ListAsync(int limit = 50, CancellationToken cancellationToken = default);

    Task<RunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default);
}

/// <summary>SQLite run log, kept in its own file so agents' SQL tools can never read it.</summary>
public sealed class SqliteRunStore(IOptions<StorageOptions> options) : IRunStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "runs.db"),
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetFullPath(options.Value.DataDirectory));
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS agent_runs (
                run_id TEXT PRIMARY KEY, started_at TEXT NOT NULL, duration_ms REAL NOT NULL, status TEXT NOT NULL,
                provider TEXT NOT NULL, customer_email TEXT NOT NULL, message TEXT NOT NULL, intent TEXT NULL,
                language TEXT NULL, reply TEXT NULL, input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
                cost_usd REAL NOT NULL, budget_usd REAL NOT NULL, llm_calls INTEGER NOT NULL, tool_calls INTEGER NOT NULL,
                approvals INTEGER NOT NULL, error TEXT NULL);
            CREATE TABLE IF NOT EXISTS agent_run_events (
                run_id TEXT NOT NULL, seq INTEGER NOT NULL, ts TEXT NOT NULL, kind TEXT NOT NULL, agent TEXT NULL,
                name TEXT NULL, detail TEXT NULL, input_tokens INTEGER NULL, output_tokens INTEGER NULL,
                cost_usd REAL NULL, duration_ms REAL NULL, PRIMARY KEY (run_id, seq));
            """);
    }

    public async Task SaveAsync(RunRecord record, CancellationToken cancellationToken = default)
    {
        var s = record.Summary;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT OR REPLACE INTO agent_runs VALUES (@RunId, @StartedAt, @DurationMs, @Status, @Provider, @CustomerEmail,
                @Message, @Intent, @Language, @Reply, @InputTokens, @OutputTokens, @CostUsd, @BudgetUsd, @LlmCalls,
                @ToolCalls, @Approvals, @Error)
            """,
            new
            {
                s.RunId, StartedAt = s.StartedAt.ToString("O", CultureInfo.InvariantCulture), s.DurationMs,
                Status = s.Status.ToString(), s.Provider, s.CustomerEmail, s.Message, Intent = s.Intent?.ToString(),
                s.Language, s.Reply, s.InputTokens, s.OutputTokens, CostUsd = (double)s.CostUsd,
                BudgetUsd = (double)s.BudgetUsd, s.LlmCalls, s.ToolCalls, s.Approvals, s.Error,
            },
            transaction);

        await connection.ExecuteAsync(
            """
            INSERT OR REPLACE INTO agent_run_events VALUES (@RunId, @Sequence, @Timestamp, @Kind, @Agent, @Name, @Detail,
                @InputTokens, @OutputTokens, @CostUsd, @DurationMs)
            """,
            record.Events.Select(e => new
            {
                e.RunId, e.Sequence, Timestamp = e.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                Kind = e.Kind.ToString(), Agent = e.Agent?.ToString(), e.Name, e.Detail, e.InputTokens,
                e.OutputTokens, CostUsd = (double?)e.CostUsd, e.DurationMs,
            }),
            transaction);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RunSummary>> ListAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<RunRow>(new CommandDefinition(
            "SELECT * FROM agent_runs ORDER BY started_at DESC LIMIT @limit",
            new { limit },
            cancellationToken: cancellationToken));
        return rows.Select(r => r.ToSummary()).ToList();
    }

    public async Task<RunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            "SELECT * FROM agent_runs WHERE run_id = @runId",
            new { runId },
            cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        var events = await connection.QueryAsync<EventRow>(new CommandDefinition(
            "SELECT * FROM agent_run_events WHERE run_id = @runId ORDER BY seq",
            new { runId },
            cancellationToken: cancellationToken));
        return new RunRecord(row.ToSummary(), events.Select(e => e.ToEvent()).ToList());
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    // Row types mirror SQLite storage classes (TEXT/INTEGER/REAL) and convert explicitly.
    private sealed class RunRow
    {
        public string run_id { get; set; } = "";
        public string started_at { get; set; } = "";
        public double duration_ms { get; set; }
        public string status { get; set; } = "";
        public string provider { get; set; } = "";
        public string customer_email { get; set; } = "";
        public string message { get; set; } = "";
        public string? intent { get; set; }
        public string? language { get; set; }
        public string? reply { get; set; }
        public long input_tokens { get; set; }
        public long output_tokens { get; set; }
        public double cost_usd { get; set; }
        public double budget_usd { get; set; }
        public long llm_calls { get; set; }
        public long tool_calls { get; set; }
        public long approvals { get; set; }
        public string? error { get; set; }

        public RunSummary ToSummary() => new()
        {
            RunId = run_id,
            StartedAt = DateTimeOffset.Parse(started_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DurationMs = duration_ms,
            Status = Enum.Parse<RunStatus>(status),
            Provider = provider,
            CustomerEmail = customer_email,
            Message = message,
            Intent = Enum.TryParse<SupportIntent>(intent, out var parsed) ? parsed : null,
            Language = language,
            Reply = reply,
            InputTokens = input_tokens,
            OutputTokens = output_tokens,
            CostUsd = (decimal)cost_usd,
            BudgetUsd = (decimal)budget_usd,
            LlmCalls = (int)llm_calls,
            ToolCalls = (int)tool_calls,
            Approvals = (int)approvals,
            Error = error,
        };
    }

    private sealed class EventRow
    {
        public string run_id { get; set; } = "";
        public long seq { get; set; }
        public string ts { get; set; } = "";
        public string kind { get; set; } = "";
        public string? agent { get; set; }
        public string? name { get; set; }
        public string? detail { get; set; }
        public long? input_tokens { get; set; }
        public long? output_tokens { get; set; }
        public double? cost_usd { get; set; }
        public double? duration_ms { get; set; }

        public RunEvent ToEvent() => new()
        {
            RunId = run_id,
            Sequence = (int)seq,
            Timestamp = DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Kind = Enum.Parse<RunEventKind>(kind),
            Agent = Enum.TryParse<AgentRole>(agent, out var role) ? role : null,
            Name = name,
            Detail = detail,
            InputTokens = input_tokens,
            OutputTokens = output_tokens,
            CostUsd = (decimal?)cost_usd,
            DurationMs = duration_ms,
        };
    }
}
