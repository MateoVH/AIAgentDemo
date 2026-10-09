using AIAgentDemo.Core.Agents;
using AIAgentDemo.Core.Costs;
using AIAgentDemo.Core.Data;
using AIAgentDemo.Core.Orchestration;
using AIAgentDemo.Core.Providers;
using AIAgentDemo.Core.Tracing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIAgentDemo.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the multi-agent support pipeline. The host must also register an
    /// <see cref="IApprovalGateway"/> (human-in-the-loop UI, or <see cref="AutoApprovalGateway"/>).
    /// </summary>
    public static IServiceCollection AddAgentDesk(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection("AI"));
        services.Configure<BudgetOptions>(configuration.GetSection("Budget"));
        services.Configure<PricingOptions>(configuration.GetSection("Pricing"));
        services.Configure<StorageOptions>(configuration.GetSection("Storage"));
        services.Configure<ApprovalOptions>(configuration.GetSection("Approval"));

        // Hosts normally register real logging first; these only fill the gap for bare containers.
        services.TryAddSingleton<ILoggerFactory, NullLoggerFactory>();
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(NullLogger<>)));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<StoreDatabase>();
        services.AddSingleton<StoreRepository>();
        services.AddSingleton<PricingCatalog>();
        services.AddSingleton<IChatClientProvider, ChatClientProvider>();
        services.AddSingleton<SupportAgentFactory>();
        services.AddSingleton<IRunStore, SqliteRunStore>();
        services.AddSingleton<SupportSupervisor>();
        return services;
    }

    /// <summary>Creates and seeds the SQLite files. Call once at startup.</summary>
    public static async Task InitializeAgentDeskAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await services.GetRequiredService<StoreDatabase>().InitializeAsync(cancellationToken);
        await services.GetRequiredService<IRunStore>().InitializeAsync(cancellationToken);
    }
}
