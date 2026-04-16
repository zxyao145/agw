using Agw.Jobs.Application.Services;
using Agw.Jobs.Executors.Abstractions;
using Agw.Jobs.Executors.Cluster;
using Agw.Jobs.Executors.Common;
using Agw.Jobs.Executors.StandAlone;
using Agw.Jobs.External;
using Agw.Jobs.External.Cluster;
using Agw.Jobs.External.StandAlone;
using Agw.Jobs.HostedService;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using StackExchange.Redis;

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
        services.AddScoped<JobAppService>();


        var workerPoolMode = configuration.GetValue<string>("Jobs:WorkerPool:Mode") ?? "SingleNode";
        if (string.Equals(workerPoolMode, "Cluster", StringComparison.OrdinalIgnoreCase))
        {
            var redisConnectionString = configuration.GetValue<string>("Redis:ConnectionString") ?? "localhost:6379,abortConnect=false";
            var redisConfiguration = ConfigurationOptions.Parse(redisConnectionString);
            redisConfiguration.AbortOnConnectFail = false;
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConfiguration));

            services.AddSingleton<IProjectExecutionLock, RedisProjectExecutionLock>();
            services.AddSingleton<IJobWorkerPool, RedisJobWorkerPool>();
            services.AddSingleton<IJobWorkerNode, RedisJobWorkerNode>();
            services.AddSingleton<IJobSchedulerCoordinator, RedisJobSchedulerCoordinator>();
        }
        else
        {
            services.TryAddSingleton<IProjectExecutionLock, LocalProjectExecutionLock>();
            services.TryAddSingleton<IJobWorkerPool, LocalJobWorkerPool>();
            services.TryAddSingleton<IJobWorkerNode, LocalJobWorkerNode>();
            services.TryAddSingleton<IJobSchedulerCoordinator, PassThroughJobSchedulerCoordinator>();
        }
        return services;
    }
}
