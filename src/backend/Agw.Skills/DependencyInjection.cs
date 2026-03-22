using Agw.Domain.Services;
using Agw.Domain.Services.Skills;
using Agw.Skills.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Skills;

public static class DependencyInjection
{
    public static IServiceCollection AddSkills(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<SkillDomainService>();
        services.AddScoped<SkillAppService>();

        return services;
    }
}
