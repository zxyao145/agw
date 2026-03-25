using Agw.Jobs.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs;

public static class DependencyInjection
{
    public static IServiceCollection AddJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHostedService<ProjectTaskSchedulerHostedService>();
        services.AddHostedService<ScheduledTaskHostedService>();
        services.AddScoped<IAgentExecutor, AgentExecutor>();
        services.AddSingleton<IScheduledTaskTimeCalculator, ScheduledTaskTimeCalculator>();
        services.AddScoped<ScheduledTaskAppService>();
        return services;
    }
}
