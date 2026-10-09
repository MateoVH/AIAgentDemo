using AIAgentDemo.Core;
using AIAgentDemo.Core.Data;
using AIAgentDemo.Core.Orchestration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIAgentDemo.Tests;

/// <summary>
/// The real service graph in Demo mode (simulated model, zero latency) over a throw-away SQLite folder.
/// </summary>
internal sealed class TestHost : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aiagentdemo-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    private TestHost(bool approve, Dictionary<string, string?>? overrides, Action<IServiceCollection>? configure)
    {
        Approvals = new AutoApprovalGateway(approve, reviewer: "test-reviewer");

        var settings = new Dictionary<string, string?>
        {
            ["AI:Provider"] = "Demo",
            ["AI:Demo:LatencyMs"] = "0",
            ["Storage:DataDirectory"] = _directory,
            ["Budget:MaxCostPerRunUsd"] = "0.50",
            ["Pricing:Models:0:Model"] = "demo-simulated",
            ["Pricing:Models:0:InputPerMillion"] = "4",
            ["Pricing:Models:0:OutputPerMillion"] = "20",
        };
        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection()
            .AddSingleton<IApprovalGateway>(Approvals)
            .AddAgentDesk(configuration);
        configure?.Invoke(services);
        _services = services.BuildServiceProvider();
    }

    public AutoApprovalGateway Approvals { get; }

    public IServiceProvider Services => _services;

    public SupportSupervisor Supervisor => _services.GetRequiredService<SupportSupervisor>();

    public StoreDatabase Database => _services.GetRequiredService<StoreDatabase>();

    public StoreRepository Repository => _services.GetRequiredService<StoreRepository>();

    public static async Task<TestHost> StartAsync(
        bool approve = true,
        Dictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configure = null)
    {
        var host = new TestHost(approve, overrides, configure);
        await host._services.InitializeAgentDeskAsync();
        return host;
    }

    public Task<RunResult> RunSampleAsync(string sampleId, string operatorLanguage = "en", decimal? budgetUsd = null)
    {
        var sample = SampleTickets.All.Single(t => t.Id == sampleId);
        return Supervisor.RunAsync(new SupportRequest
        {
            CustomerEmail = sample.CustomerEmail,
            Message = sample.Message,
            OperatorLanguage = operatorLanguage,
            BudgetUsd = budgetUsd,
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering file handle must not fail the test run.
        }
    }
}
