using Agw.Jobs.Application.Services;
using Agw.Jobs.Application.Skills;
using Agw.Jobs.Execution;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Skills.Contracts.Registration;

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
        services.AddSingleton<IAgentSkillRegistration, JobManagementSkillRegistration>();
        services.AddScoped<JobAppService>();
        return services;
    }
}
