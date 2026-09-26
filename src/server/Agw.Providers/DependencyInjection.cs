using Agw.Providers.Application;
using Agw.Providers.Application.Facades;
using Agw.Providers.Contracts.References;
using Agw.Providers.Domain.Repositories;
using Agw.Providers.Domain.Services;
using Agw.Providers.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Providers;

public static class DependencyInjection
{
    public static IServiceCollection AddProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IProviderModelRepository, ProviderModelRepository>();
        services.AddScoped<ProviderModelBindingDomainService>();
        services.AddScoped<IModelAppService, ModelAppService>();
        services.AddScoped<IProviderAppService, ProviderAppService>();
        services.AddScoped<IModelProviderAppService, ModelProviderAppService>();
        services.AddScoped<IProviderModelDiscoveryService, ProviderModelDiscoveryService>();
        services.AddScoped<IModelProviderReferenceFacade, ModelProviderReferenceFacade>();

        return services;
    }
}
