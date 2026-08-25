using Agw.Jobs.Application.Facades;
using Agw.Jobs.Application.Services;
using Agw.Jobs.Application.Skills;
using Agw.Jobs.Contracts.Metrics;
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
    public sealed record RegistrationOptions(bool AddScheduler = true, bool UseDurableExecution = false);

    public static IServiceCollection AddJobs(
        this IServiceCollection services,
        IConfiguration configuration,
        RegistrationOptions? registrationOptions = null
    )
    {
        registrationOptions ??= new RegistrationOptions();
        if (registrationOptions.AddScheduler)
        {
            services.AddHostedService<JobHostedService>();
            services.AddScoped<IJobAgentExecutor, JobAgentExecutor>();
            if (registrationOptions.UseDurableExecution)
            {
                services.AddHostedService<DurableJobRecoveryHostedService>();
            }
            services.AddScoped<JobAttemptRunner>();
            services.AddScoped<IJobAttemptOutcomeRecorder, JobAttemptOutcomeRecorder>();
            services.AddSingleton<JobSchedulerWakeSignal>();
            services.AddSingleton<JobScheduleCalculator>();
        }
        services.AddSingleton<IAgentSkillRegistration, JobManagementSkillRegistration>();
        services.AddScoped<JobAppService>();
        services.AddScoped<IJobMetricsFacade, JobMetricsFacade>();
        return services;
    }
}
