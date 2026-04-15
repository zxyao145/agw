using Agw.Jobs.Application.Services;
using Agw.Jobs.HostedService;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using StackExchange.Redis;

namespace Agw.Jobs;

public static class DependencyInjection
{
    public static IServiceCollection AddJobs(this IServiceCollection services, IConfiguration configuration)
    {
        var redisConnectionString = configuration.GetValue<string>("Redis:ConnectionString") ?? "localhost:6379,abortConnect=false";
        var redisConfiguration = ConfigurationOptions.Parse(redisConnectionString);
        redisConfiguration.AbortOnConnectFail = false;

        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConfiguration));
        services.AddSingleton<IProjectExecutionLock, RedisProjectExecutionLock>();
        services.AddHostedService<JobHostedService>();
        services.AddScoped<IAgentExecutor, AgentExecutor>();
        services.AddSingleton<IJobDomainEventDispatcher, JobDomainEventDispatcher>();
        services.AddSingleton<IJobTimeCalculator, JobTimeCalculator>();
        services.AddScoped<JobAppService>();
        return services;
    }
}
