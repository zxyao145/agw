using Agw.Skills.Application;
using Agw.Skills.Application.Facades;
using Agw.Skills.Application.Remote;
using Agw.Skills.Contracts.References;
using Agw.Skills.Contracts.Remote;
using Agw.Skills.Domain.Repositories;
using Agw.Skills.Domain.Services;
using Agw.Skills.Infrastructure;
using Agw.Skills.Infrastructure.Remote;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Skills;

public static class DependencyInjection
{
    public static IServiceCollection AddSkills(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ISkillRepository, SkillRepository>();
        services.AddScoped<SkillNameUniquenessDomainService>();
        services.AddScoped<SkillAppService>();
        services.AddScoped<ISkillReferenceFacade, SkillReferenceFacade>();
        services.AddSingleton<IRemoteSkillClient, RemoteSkillHttpClient>();
        services.AddSingleton<IRemoteSkillContentResolver, RemoteSkillContentResolver>();
        services.AddHttpClient(
            RemoteSkillHttpClient.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(10)
        );

        return services;
    }
}
