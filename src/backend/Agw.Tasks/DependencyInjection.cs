using Agw.Domain.Services;
using Agw.Shared.Contracts.Tasks;
using Agw.Tasks.Application;
using Agw.Tasks.Application.Files;
using Agw.Tasks.Domain.Services;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tasks;

public static class DependencyInjection
{
    public static IServiceCollection AddTasks(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ProjectDomainService>();
        services.AddScoped<ProjectTaskDomainService>();
        services.AddScoped<ITaskAppService, TaskAppService>();
        services.AddScoped<IProjectAppService, ProjectAppService>();
        services.AddScoped<ProjectTaskAppService>();
        services.AddScoped<ProjectResolver>();
        services.AddScoped<TaskRecordDomainService>();
        services.AddSingleton<IPathSecurityService, PathSecurityService>();

        services.AddSingleton<EfCoreChatHistoryProvider>();
        services.AddSingleton<ChatHistoryProvider>(sp =>
        {
            return sp.GetRequiredService<EfCoreChatHistoryProvider>();
        });
        services.AddSingleton<IProviderSessionState>(sp =>
        {
            return sp.GetRequiredService<EfCoreChatHistoryProvider>();
        });

        return services;
    }
}
