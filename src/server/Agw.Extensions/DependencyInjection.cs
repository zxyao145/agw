using Agw.Extensions.Application;
using Agw.Extensions.Application.Packages;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Application.Runtime;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Database;
using Agw.Extensions.Infrastructure.Packages;
using Agw.Extensions.Infrastructure.Persistence;
using Agw.Extensions.Infrastructure.Runtime;
using Agw.Extensions.Sdk.Database;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddExtensions(
        this IServiceCollection services,
        IConfiguration configuration,
        string hostRole
    )
    {
        services
            .AddOptions<ExtensionOptions>()
            .Bind(configuration.GetSection("Extensions"))
            .Configure(options => options.HostRole = hostRole)
            .Validate(
                options =>
                    options.DrainTimeout > TimeSpan.Zero
                    && options.UnloadTimeout > TimeSpan.Zero
                    && options.StatePollInterval > TimeSpan.Zero
                    && options.MaximumPackageBytes > 0
                    && options.MaximumExtractedBytes >= options.MaximumPackageBytes,
                "Extension lifecycle intervals and package limits must be positive and consistent."
            )
            .ValidateOnStart();
        services.AddSingleton<ExtensionHostIdentity>();
        services.AddSingleton<IExtensionHostIdentity>(provider => provider.GetRequiredService<ExtensionHostIdentity>());
        services.AddSingleton<ExtensionPackageCatalog>();
        services.AddSingleton<IExtensionPackageCatalog>(provider =>
            provider.GetRequiredService<ExtensionPackageCatalog>()
        );
        services.AddSingleton<IExtensionEnablementStore, SettingsExtensionEnablementStore>();
        services.AddScoped<IExtensionAdmissionValidator, ExtensionAdmissionValidator>();
        services.AddSingleton<IExtensionRuntimeAvailability, ExtensionEnablementAvailability>();
        services.AddSingleton<ExtensionCallerAccessor>();
        services.AddSingleton<IExtensionCallerAccessor>(provider =>
            provider.GetRequiredService<ExtensionCallerAccessor>()
        );
        services.AddSingleton<IExtensionCallerScope>(provider =>
            provider.GetRequiredService<ExtensionCallerAccessor>()
        );
        services.AddSingleton<ExtensionRuntime>();
        services.AddSingleton<IExtensionRuntime>(provider => provider.GetRequiredService<ExtensionRuntime>());
        services.AddSingleton<IExtensionInvoker>(provider => provider.GetRequiredService<ExtensionRuntime>());
        services.AddSingleton<IExtensionGenerationCatalog>(provider => provider.GetRequiredService<ExtensionRuntime>());
        services.AddSingleton<IExtensionGenerationAccessor>(provider =>
            provider.GetRequiredService<ExtensionRuntime>()
        );
        services.AddSingleton<ExtensionDatabaseLockCoordinator>();
        services.AddSingleton<IExtensionDatabaseTransactionCoordinator>(provider =>
            provider.GetRequiredService<ExtensionDatabaseLockCoordinator>()
        );
        services.AddSingleton<IExtensionDatabase, ExtensionDatabase>();
        services.AddSingleton<ExtensionEpochValidator>();
        services.AddSingleton<IExtensionEpochValidator>(provider =>
            provider.GetRequiredService<ExtensionEpochValidator>()
        );
        services.AddSingleton<IExtensionSchemaEpochValidator>(provider =>
            provider.GetRequiredService<ExtensionEpochValidator>()
        );
        services.AddSingleton<ExtensionPackageCache>();
        services.AddSingleton<ExtensionEndpointDataSource>();
        services.AddScoped<ExtensionPackageService>();
        services.AddScoped<ExtensionManagementService>();
        services.AddSingleton<ExtensionNodeWorker>();
        services.AddSingleton<IExtensionNodeReconciler>(provider => provider.GetRequiredService<ExtensionNodeWorker>());
        services.AddHostedService(provider => provider.GetRequiredService<ExtensionNodeWorker>());
        return services;
    }

    public static void MapExtensionEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.DataSources.Add(endpoints.ServiceProvider.GetRequiredService<ExtensionEndpointDataSource>());
}
