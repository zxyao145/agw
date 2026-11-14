using Agw.Jobs.Application.Facades;
using Agw.Jobs.Application.Services;
using Agw.Jobs.Application.Skills;
using Agw.Jobs.Contracts.Metrics;
using Agw.Jobs.Domain.Services;
using Agw.Skills.Contracts.Registration;
using Agw.Tools.Abstractions.Generated;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs;

public static class DependencyInjection
{
    public static IServiceCollection AddJobs(this IServiceCollection services)
    {
        services.AddSingleton<IAgentSkillRegistration, JobManagementSkillRegistration>();
        services.AddSingleton<IAgwGeneratedToolModule>(Agw.Generated.Agw.Jobs.AgwToolModule.Instance);
        services.AddScoped<Application.Tools.JobManagementToolExecutor>();
        services.AddScoped<JobDefinitionDomainService>();
        services.AddScoped<JobAppService>();
        services.AddScoped<IJobMetricsFacade, JobMetricsFacade>();
        return services;
    }
}
