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
        services.TryAddSingleton<DatabaseInitializationStateStore>();
        services
            .AddSingleton(configuredSetup ?? ConfiguredSetupBootstrap.None)
            .AddSingleton<IAuthenticationStateReader>(provider =>
                provider.GetRequiredService<DatabaseInitializationStateStore>()
            )
            .AddSingleton<IServerInitializationState>(provider =>
                provider.GetRequiredService<DatabaseInitializationStateStore>()
            )
            .AddHostedService<DatabaseInitializationStateRefreshHostedService>();

        if (!readOnly)
        {
            services
                .AddSingleton<IInitializationStateStore>(provider =>
                    provider.GetRequiredService<DatabaseInitializationStateStore>()
                )
                .AddSingleton<IAuthenticationStateStore>(provider =>
                    provider.GetRequiredService<DatabaseInitializationStateStore>()
                )
                .AddSingleton<SetupCodeService>()
                .AddScoped<ConfiguredSetupInitializer>()
                .AddScoped<ISetupInitializationService, SetupInitializationService>();
        }

        return services;
    }
}
