using Agw.Auth.Contracts;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agw.Setup.Services;

public static class DependencyInjection
{
    public static IServiceCollection AddSetup(
        this IServiceCollection services,
        IConfiguration configuration,
        ConfiguredSetupBootstrap? configuredSetup = null,
        bool readOnly = false
    )
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<JsonInitializationStateStore>();
        services
            .AddSingleton(configuredSetup ?? ConfiguredSetupBootstrap.None)
            .AddSingleton<IAuthenticationStateReader>(provider =>
                provider.GetRequiredService<JsonInitializationStateStore>()
            )
            .AddSingleton<IServerInitializationState>(provider =>
                provider.GetRequiredService<JsonInitializationStateStore>()
            )
            .AddHostedService<JsonInitializationStateRefreshHostedService>();

        if (!readOnly)
        {
            services
                .AddSingleton<IInitializationStateStore>(provider =>
                    provider.GetRequiredService<JsonInitializationStateStore>()
                )
                .AddSingleton<IAuthenticationStateStore>(provider =>
                    provider.GetRequiredService<JsonInitializationStateStore>()
                )
                .AddSingleton<SetupCodeService>()
                .AddScoped<ConfiguredSetupInitializer>()
                .AddScoped<LegacyApiTokenMigrator>()
                .AddScoped<ISetupInitializationService, SetupInitializationService>();
        }

        return services;
    }
}
