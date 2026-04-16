using Agw.Jobs.Application.Services;
using Agw.Jobs.Executors.Abstractions;
using Agw.Jobs.Executors.Common;
using Agw.Jobs.Executors.StandAlone;
using Agw.Jobs.HostedService;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agw.Jobs;

public static class DependencyInjection
{
    public static IServiceCollection AddJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JobSchedulerOptions>(configuration.GetSection("Jobs:Scheduler"));
        services.Configure<JobWorkerOptions>(configuration.GetSection("Jobs:Worker"));
        services.Configure<JobWorkerPoolOptions>(configuration.GetSection("Jobs:WorkerPool"));

        services.AddHostedService<JobHostedService>();
        services.AddScoped<IAgentExecutor, AgentExecutor>();
        services.AddSingleton<IJobDomainEventDispatcher, JobDomainEventDispatcher>();
        services.AddSingleton<IJobTimeCalculator, JobTimeCalculator>();
        services.AddSingleton<IJobScheduler, JobScheduler>();
        services.AddSingleton<IJobWorker, JobWorker>();
        services.TryAddSingleton<IJobWorkerPool, LocalJobWorkerPool>();
        services.TryAddSingleton<IJobWorkerNode, LocalJobWorkerNode>();
        services.TryAddSingleton<IJobSchedulerCoordinator, PassThroughJobSchedulerCoordinator>();
        services.AddScoped<JobAppService>();
        return services;
    }
}
