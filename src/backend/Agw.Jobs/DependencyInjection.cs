using Agw.Jobs.Application.Services;
using Agw.Jobs.HostedService;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs;

public static class DependencyInjection
{
    public static IServiceCollection AddJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHostedService<JobHostedService>();
        services.AddScoped<IAgentExecutor, AgentExecutor>();
        services.AddSingleton<IJobTimeCalculator, JobTimeCalculator>();
        services.AddScoped<JobAppService>();
        return services;
    }
}
