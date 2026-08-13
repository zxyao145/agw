using Agw.Auth.Application;
using Agw.Shared.Runtime;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Setup.Services;

public static class DependencyInjection
{
    public static IServiceCollection AddSetup(
        this IServiceCollection services,
        IConfiguration configuration,
        ConfiguredSetupBootstrap? configuredSetup = null)
    {
        services
            .AddSingleton(configuredSetup ?? ConfiguredSetupBootstrap.None)
            .AddSingleton<JsonInitializationStateStore>()
            .AddSingleton<IInitializationStateStore>(provider => provider.GetRequiredService<JsonInitializationStateStore>())
            .AddSingleton<IAuthenticationStateStore>(provider => provider.GetRequiredService<JsonInitializationStateStore>())
            .AddSingleton<IServerInitializationState>(provider => provider.GetRequiredService<JsonInitializationStateStore>())
            .AddSingleton<SetupCodeService>()
            .AddScoped<ConfiguredSetupInitializer>()
            .AddScoped<LegacyApiTokenMigrator>()
            .AddScoped<ISetupInitializationService, SetupInitializationService>();

        return services;
    }
}
