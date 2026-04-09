using Agw.Providers.Application;
using Agw.Providers.Domain.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Providers;

public static class DependencyInjection
{
    public static IServiceCollection AddProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ModelDomainService>();
        services.AddScoped<ProviderDomainService>();
        services.AddScoped<ModelProviderDomainService>();
        services.AddScoped<IModelAppService, ModelAppService>();
        services.AddScoped<IProviderAppService, ProviderAppService>();
        services.AddScoped<IModelProviderAppService, ModelProviderAppService>();

        return services;
    }
}
