using Agw.Files.Abstracts;
using Agw.Projects.Application;
using Agw.Projects.Application.Facades;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Metrics;
using Agw.Projects.Contracts.Runtime;
using Agw.Projects.Domain.Services;
using Agw.Projects.Infrastructure;
using Agw.Shared.Contracts.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Projects;

public static class DependencyInjection
{
    public static IServiceCollection AddProjects(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ProjectDomainService>();
        services.AddScoped<ITaskAppService, TaskAppService>();
        services.AddScoped<IProjectAppService, ProjectAppService>();
        services.AddScoped<IProjectFileSystemConfigurationProvider, ProjectFileSystemConfigurationProvider>();
        services.AddScoped<ITaskSessionBindingService, TaskSessionBindingService>();
        services.AddScoped<TaskExecutionAppService>();
        services.AddScoped<IProjectTaskFacade, ProjectTaskFacade>();
        services.AddScoped<IProjectProviderSessionFacade, ProjectProviderSessionFacade>();
        services.AddScoped<IExternalTaskSnapshotStore, ExternalTaskSnapshotStore>();
        services.AddScoped<IProjectRuntimeFacade, ProjectRuntimeFacade>();
        services.AddScoped<IProjectMetricsFacade, ProjectMetricsFacade>();
        services.AddScoped<ProjectConversationAppService>();
        services.AddScoped<ProjectResolver>();
        services.AddScoped<ProjectConversationChatHistoryDomainService>();
        services.AddScoped<IConversationHandoffProvider, ConversationHandoffProvider>();

        services.AddSingleton<EfCoreChatHistoryProvider>();
        services.AddSingleton<ChatHistoryProvider>(sp =>
        {
            return sp.GetRequiredService<EfCoreChatHistoryProvider>();
        });
        services.AddSingleton<IProviderSessionState>(sp =>
        {
            return sp.GetRequiredService<EfCoreChatHistoryProvider>();
        });
        services.AddSingleton<IConversationHistoryWriter>(sp =>
        {
            return sp.GetRequiredService<EfCoreChatHistoryProvider>();
        });
        services.AddSingleton<IAgentUsageRecorder, AgentUsageRecorder>();

        return services;
    }
}
