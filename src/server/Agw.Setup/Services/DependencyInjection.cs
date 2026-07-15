using Agw.Shared.Runtime;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Setup.Services;

public static class DependencyInjection
{
    public static IServiceCollection AddSetup(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddSingleton<JsonInitializationStateStore>()
            .AddSingleton<IInitializationStateStore>(provider => provider.GetRequiredService<JsonInitializationStateStore>())
            .AddSingleton<IServerInitializationState>(provider => provider.GetRequiredService<JsonInitializationStateStore>())
            .AddSingleton<SetupCodeService>()
            .AddSingleton<AuthenticationAttemptLimiter>()
            .AddSingleton<IPasswordHasher<object>, PasswordHasher<object>>()
            .AddScoped<ISetupInitializationService, SetupInitializationService>();

        return services;
    }
}
