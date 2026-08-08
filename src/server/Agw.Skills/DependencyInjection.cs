using Agw.Domain.Services.Skills;
using Agw.Skills.Application;
using Agw.Skills.Application.Remote;
using Agw.Skills.Infrastructure.Remote;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Skills;

public static class DependencyInjection
{
    public static IServiceCollection AddSkills(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<SkillDomainService>();
        services.AddScoped<SkillAppService>();
        services.AddSingleton<IRemoteSkillClient, RemoteSkillHttpClient>();
        services.AddSingleton<IRemoteSkillContentResolver, RemoteSkillContentResolver>();
        services.AddHttpClient(
            RemoteSkillHttpClient.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(10));

        return services;
    }
}
