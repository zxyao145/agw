using Agw.Files.Abstracts;
using Agw.Projects.Application.Facades;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Metrics;
using Agw.Projects.Contracts.Runtime;
using Agw.Projects.Domain.Services;
using Agw.Projects.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Projects;

public static class DependencyInjection
{
    public static IServiceCollection AddProjects(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<
            Agw.Projects.Contracts.IUserProjectInitializer,
            Agw.Projects.Application.UserProjectInitializer
        >();
        services.AddScoped<ConversationHistoryDomainService>();
        services.AddScoped<ProjectResourceBindingDomainService>();
        services.AddScoped<ITaskAppService, TaskAppService>();
        services.AddScoped<IProjectAppService, ProjectAppService>();
        services.AddScoped<IProjectFileSystemConfigurationProvider, ProjectFileSystemConfigurationProvider>();
        services.AddScoped<IProjectDefaultResolver, ProjectDefaultResolver>();
        services.AddScoped<IProjectOwnershipFacade, ProjectOwnershipFacade>();
        services.AddScoped<ITaskSessionBindingService, TaskSessionBindingService>();
        services.AddScoped<TaskExecutionAppService>();
        services.AddScoped<IProjectTaskFacade, ProjectTaskFacade>();
        services.AddScoped<IProjectProviderSessionFacade, ProjectProviderSessionFacade>();
        services.AddScoped<IExternalTaskSnapshotStore, ExternalTaskSnapshotStore>();
        services.AddScoped<IProjectRuntimeFacade, ProjectRuntimeFacade>();
        services.AddScoped<IProjectMetricsFacade, ProjectMetricsFacade>();
        services.AddScoped<ProjectConversationAppService>();
        services.AddScoped<ProjectResolver>();
        services.AddScoped<IConversationHandoffProvider, ConversationHandoffProvider>();

        services
            .AddOptions<ConversationHistoryOptions>()
            .Bind(configuration.GetSection(ConversationHistoryOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.Mode), "Unknown conversation history mode.")
            .Validate(
                options =>
                    options.FlushIntervalSeconds > 0 && options.FlushIntervalSeconds <= (uint.MaxValue - 1) / 1000,
                "History flush interval must be positive and fit the supported timer range."
            )
            .Validate(options => options.MaxBufferedBytes > 0, "History buffer limit must be positive.")
            .ValidateOnStart();
        services.AddSingleton<ConversationHistoryStore>();
        services.AddSingleton<Agw.Projects.Contracts.History.IConversationHistoryStore>(sp =>
            sp.GetRequiredService<ConversationHistoryStore>()
        );
        services.AddScoped<Agw.Projects.Contracts.History.IConversationTurnStore, ConversationTurnStore>();
        services.AddScoped<ConversationTurnQueryService>();
        services.AddSingleton<IAgentUsageRecorder, AgentUsageRecorder>();

        return services;
    }
}
