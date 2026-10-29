using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Definitions.Facades;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Inbound.Facades;
using Agw.Agents.Execution.Messaging.Durable;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Agw.Files;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Auth;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Jobs;
using Agw.Infrastructure.Projects;
using Agw.Infrastructure.Repositories;
using Agw.Infrastructure.Settings;
using Agw.Integrations.Application.Facades;
using Agw.Integrations.Application.Persistence;
using Agw.Integrations.Contracts.References;
using Agw.Jobs.Application.Persistence;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Projects.Application;
using Agw.Projects.Application.Facades;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.History;
using Agw.Projects.Contracts.Runtime;
using Agw.Projects.Domain.Services;
using Agw.Projects.Infrastructure;
using Agw.Settings;
using Agw.Settings.Application.Persistence;
using Agw.Setup.Services;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Runtime;
using Agw.Skills.Application.Facades;
using Agw.Skills.Application.Persistence;
using Agw.Skills.Contracts.References;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs.Tests;

internal static class JobTestServices
{
    public static ServiceProvider Create(
        DbContextOptions<AgwDbContext> options,
        TimeProvider clock,
        IProjectExecutionLock projectLock
    )
    {
        var services = new ServiceCollection();
        AddRecoveryServices(services, options, clock, projectLock);
        services.AddScoped<IAgentsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddScoped<ISkillsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddScoped<IIntegrationsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddScoped<IAgentCatalogFacade, AgentCatalogFacade>();
        services.AddScoped<ISkillReferenceFacade, SkillReferenceFacade>();
        services.AddScoped<IConnectionReferenceFacade, ConnectionReferenceFacade>();
        services.AddScoped<ProjectResourceBindingDomainService>();
        services.AddScoped<IProjectDeletionCoordinator, ProjectDeletionCoordinator>();
        services.AddScoped<IProjectAppService, ProjectAppService>();
        services.AddScoped<IProjectRuntimeFacade, ProjectRuntimeFacade>();
        services.AddScoped<IConversationTurnStore, ConversationTurnStore>();
        services.AddScoped<ITurnAcceptanceWriter, TurnAcceptanceWriter>();
        services.AddScoped<TurnAcceptanceService>();
        services.AddScoped<AgentExecutionFacade>();
        services.AddScoped<IAgentExecutionFacade>(provider => provider.GetRequiredService<AgentExecutionFacade>());
        services.AddScoped<IDurableAgentExecutionFacade>(provider =>
            provider.GetRequiredService<AgentExecutionFacade>()
        );
        services.AddOptions<ExecutionRuntimeOptions>();
        services.AddSingleton<TurnBroadcastRegistry>();
        services.AddSingleton<DurableExecutionCoordinator>();
        services.AddSingleton<DurableExecutionEventLog>();
        services.AddSingleton<IDurableExecutionLeases, DurableExecutionLeases>();
        services.AddScoped<IDurableExecutionEventSequence, DurableExecutionEventSequence>();
        services.AddScoped<DurableExecutionStore>();
        services.AddScoped<IDurableExecutionScopeMaintenance, DurableExecutionScopeMaintenance>();
        return services.BuildServiceProvider();
    }

    public static void AddRecoveryServices(
        IServiceCollection services,
        DbContextOptions<AgwDbContext> options,
        TimeProvider clock,
        IProjectExecutionLock executionLock
    )
    {
        var configuration = new ConfigurationBuilder().Build();
        var dataPaths = AgwDataPaths.Resolve(
            Path.Combine(AppContext.BaseDirectory, ".scratch", "job-test-services", "data"),
            Path.Combine(AppContext.BaseDirectory, ".scratch", "job-test-services", "home")
        );
        dataPaths.EnsureCreated();
        services.AddSingleton(dataPaths);
        services.AddFiles(configuration);
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddAuth();
        services.AddSingleton<IProjectExecutionLock>(executionLock);
        services.AddSingleton<IApplicationLock>(InMemoryApplicationLock.Shared);
        services.AddScoped<AgwDbContext>(_ => new AgwDbContext(options));
        services.AddScoped<DbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddScoped<IJobsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddScoped<IProjectsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddScoped<ISettingsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddScoped<ISettingsPersistence, EfSettingsPersistence>();
        services.AddSettings();
        services.AddScoped<IServerAuthStatePersistence, SettingsServerAuthStatePersistence>();
        services.AddSetup(configuration);
        services.AddScoped<IJobStore, JobRepo>();
        services.AddScoped<IJobOutcomeTransaction, JobOutcomeTransaction>();
        services.AddScoped<ProjectResolver>();
        services.AddScoped<ConversationHistoryDomainService>();
        services.AddScoped<IConversationCreatorStore, ConversationCreatorStore>();
        services.AddScoped<TaskExecutionAppService>();
        services.AddScoped<TaskAppService>();
        services.AddScoped<ITaskAppService>(provider => provider.GetRequiredService<TaskAppService>());
        services.AddScoped<IProjectTaskFacade, ProjectTaskFacade>();
        services.AddJobs(configuration, new DependencyInjection.RegistrationOptions());
    }
}
