using Agw.Domain.Repositories;
using Agw.Jobs.Services;
using Agw.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs;

public static class DependencyInjection
{
    public static IServiceCollection AddJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHostedService<ProjectTaskSchedulerHostedService>();
        return services;
    }
}
