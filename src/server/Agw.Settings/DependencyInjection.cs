using Agw.Settings.Application;
using Agw.Settings.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Settings;

public static class DependencyInjection
{
    public static IServiceCollection AddSettings(this IServiceCollection services)
    {
        services.AddScoped<ISettingsStore, SettingsStore>();
        return services;
    }
}
