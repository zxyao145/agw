using Agw.Jobs.Application.Services;
using Agw.Jobs.Execution;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Jobs.Scheduling.Coordination;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs;

public static class DependencyInjection
{
    public static IServiceCollection AddJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHostedService<JobHostedService>();
        services.AddScoped<IJobAgentExecutor, JobAgentExecutor>();
        services.AddScoped<JobAttemptRunner>();
        services.AddSingleton<JobSchedulerWakeSignal>();
        services.AddSingleton<JobScheduleCalculator>();
        services.AddScoped<JobAppService>();
        return services;
    }
}
