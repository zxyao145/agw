using Agw.Auth.Contracts;
using Agw.Infrastructure.Auth;
using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Coordination;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Encryption;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Infrastructure.Jobs;
using Agw.Infrastructure.Repositories;
using Agw.Infrastructure.Skills;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Skills.Contracts.Remote;
using Medallion.Threading;
using Medallion.Threading.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDataProtection();
        services.AddSingleton<IEncryptedDataProtector, DataProtectionEncryptedDataProtector>();

        var configuredDatabaseProvider = configuration[$"{DatabaseSettings.SectionName}:Provider"];
        if (configuredDatabaseProvider != null)
        {
            DatabaseProviderResolver.Parse(configuredDatabaseProvider);
        }

        var configuredDistributedLockProvider = configuration[$"{DistributedLockSettings.SectionName}:Provider"];
        if (configuredDistributedLockProvider != null)
        {
            DistributedLockSettingsResolver.ParseProvider(configuredDistributedLockProvider);
        }

        var databaseSettings =
            configuration.GetSection(DatabaseSettings.SectionName).Get<DatabaseSettings>() ?? new DatabaseSettings();
        var distributedLockSettings =
            configuration.GetSection(DistributedLockSettings.SectionName).Get<DistributedLockSettings>()
            ?? new DistributedLockSettings();
        if (distributedLockSettings.Provider.HasValue)
        {
            DistributedLockSettingsResolver.Resolve(
                distributedLockSettings,
                databaseSettings.Provider,
                databaseSettings.ConnectionString
            );
        }

        services.Configure<DatabaseSettings>(configuration.GetSection(DatabaseSettings.SectionName));
        services.Configure<DistributedLockSettings>(configuration.GetSection(DistributedLockSettings.SectionName));
        services.AddScoped<EntityCreatorInterceptor>();
        services.AddScoped<EntityModifierInterceptor>();
        services.AddScoped<EntitySoftDeleteInterceptor>();
        services.AddDbContext<AgwDbContext>(
            (serviceProvider, options) =>
            {
                var settings = serviceProvider.GetRequiredService<IOptionsMonitor<DatabaseSettings>>().CurrentValue;

                var paths = serviceProvider.GetRequiredService<AgwDataPaths>();
                settings = new DatabaseSettings
                {
                    Provider = settings.Provider,
                    ConnectionString = DatabaseConnectionStringResolver.Resolve(
                        settings.Provider,
                        settings.ConnectionString,
                        paths
                    ),
                };

                AgwDbContextOptionsConfigurator.Configure(options, settings.Provider, settings.ConnectionString);
                options.AddInterceptors(
                    serviceProvider.GetRequiredService<EntityCreatorInterceptor>(),
                    serviceProvider.GetRequiredService<EntityModifierInterceptor>(),
                    serviceProvider.GetRequiredService<EntitySoftDeleteInterceptor>()
                );
                options.ReplaceService<IMigrationsModelDiffer, NoForeignKeyModelDiffer>();
            }
        );

        // Register database seeder
        services.AddScoped<DbSeeder>();
        services.AddScoped<IApiTokenStore, EfApiTokenStore>();

        services.AddScoped<DbContext>(serviceProvider => serviceProvider.GetRequiredService<AgwDbContext>());
        services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
        services.AddScoped<JobRepo>();
        services.AddScoped<IRepository<Job>, JobRepo>(sp => sp.GetRequiredService<JobRepo>());
        services.AddScoped<IJobStore, JobRepo>(sp => sp.GetRequiredService<JobRepo>());
        services.AddScoped<IUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<AgwDbContext>());

        services.AddSingleton<InMemoryProjectExecutionLock>();
        services.AddSingleton<Func<DistributedLockProvider, string, IDistributedLockProvider>>(_ =>
            (provider, connectionString) =>
                provider switch
                {
                    DistributedLockProvider.Postgres => new PostgresDistributedSynchronizationProvider(
                        connectionString
                    ),
                    _ => throw new AgwException(
                        ErrorCodes.UnsupportedDistributedLockProvider,
                        $"Distributed lock provider '{provider}' is not supported."
                    ),
                }
        );
        services.AddSingleton<IProjectExecutionLock, ProjectExecutionLockRouter>();
        services.AddSingleton<IRemoteSkillRefreshLock, RemoteSkillRefreshLockRouter>();
        services.AddSingleton<InMemoryApplicationLock>();
        services.AddSingleton<IApplicationLock, ApplicationLockRouter>();

        return services;
    }
}
