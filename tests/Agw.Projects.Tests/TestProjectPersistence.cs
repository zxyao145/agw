using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Catalog;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Projects.Tests;

internal static class TestProjectPersistence
{
    public static ProjectDeletionCoordinator CreateDeletionCoordinator(AgwDbContext context) =>
        new(
            context,
            InMemoryApplicationLock.Shared,
            new DurableExecutionScopeMaintenance(
                context,
                TimeProvider.System,
                NullLogger<DurableExecutionScopeMaintenance>.Instance
            ),
            TimeProvider.System
        );

    /// <summary>
    /// 协调器的每次操作都从这里创建独立 scope，scope 中的 DbContext 使用测试数据库的同一组选项。
    /// Each coordinator operation creates its own scope here, whose DbContext uses the test database's options.
    /// </summary>
    public static ServiceProvider CreateProviderSessionServices(
        DbContextOptions<AgwDbContext> options,
        TimeProvider? timeProvider = null
    )
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(timeProvider ?? TimeProvider.System);
        services.AddSingleton<IApplicationLock>(InMemoryApplicationLock.Shared);
        services.AddScoped(_ => new AgwDbContext(options));
        services.AddScoped<IConversationExecutionGate, ConversationExecutionGate>();
        services.AddScoped<IDurableExecutionScopeMaintenance, DurableExecutionScopeMaintenance>();
        services.AddSingleton<IProjectProviderSessionCoordinator, ProjectProviderSessionCoordinator>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public static TaskSessionBindingService CreateBindingService(
        AgwDbContext context,
        IServiceProvider services,
        IAgentCatalogFacade agentCatalog,
        IUserInfoService? userInfo = null
    ) =>
        new(
            context,
            services.GetRequiredService<IProjectProviderSessionCoordinator>(),
            services.GetRequiredService<TimeProvider>(),
            userInfo ?? new TestUserInfoService(),
            agentCatalog
        );
}
