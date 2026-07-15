using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Jobs;
using Agw.Infrastructure.Repositories;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Shared.Configuration;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;

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

        var databaseSettings = configuration
            .GetSection(DatabaseSettings.SectionName)
            .Get<DatabaseSettings>() ?? new DatabaseSettings();
        var distributedLockSettings = configuration
            .GetSection(DistributedLockSettings.SectionName)
            .Get<DistributedLockSettings>() ?? new DistributedLockSettings();
        if (distributedLockSettings.Provider.HasValue)
        {
            DistributedLockSettingsResolver.Resolve(
                distributedLockSettings,
                databaseSettings.Provider,
                databaseSettings.ConnectionString);
        }

        services.Configure<DatabaseSettings>(configuration.GetSection(DatabaseSettings.SectionName));
        services.Configure<DistributedLockSettings>(
            configuration.GetSection(DistributedLockSettings.SectionName));
        services.AddDbContext<AgwDbContext>((serviceProvider, options) =>
        {
            var settings = serviceProvider
                .GetRequiredService<IOptionsMonitor<DatabaseSettings>>()
                .CurrentValue;

            var paths = serviceProvider.GetRequiredService<AgwDataPaths>();
            settings = new DatabaseSettings
            {
                Provider = settings.Provider,
                ConnectionString = DatabaseConnectionStringResolver.Resolve(settings.Provider, settings.ConnectionString, paths)
            };

            ConfigureDatabaseProvider(options, settings);
            options.ReplaceService<IMigrationsModelDiffer, NoForeignKeyModelDiffer>();
        });

        // Register database seeder
        services.AddScoped<DbSeeder>();

        services.AddScoped<DbContext, AgwDbContext>();
        services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
        services.AddScoped<JobRepo>();
        services.AddScoped<IRepository<Job>, JobRepo>(sp => sp.GetRequiredService<JobRepo>());
        services.AddScoped<IJobStore, JobRepo>(sp => sp.GetRequiredService<JobRepo>());
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddSingleton<InMemoryProjectExecutionLock>();
        services.AddSingleton<Func<DistributedLockProvider, string, IDistributedLockProvider>>(_ =>
            (provider, connectionString) => provider switch
            {
                DistributedLockProvider.Postgres =>
                    new PostgresDistributedSynchronizationProvider(connectionString),
                _ => throw new AgwException(
                    ErrorCodes.UnsupportedDistributedLockProvider,
                    $"Distributed lock provider '{provider}' is not supported.")
            });
        services.AddSingleton<IProjectExecutionLock, ProjectExecutionLockRouter>();

        return services;
    }

    private static void ConfigureDatabaseProvider(DbContextOptionsBuilder options, DatabaseSettings settings)
    {
        if (settings.Provider == DatabaseProvider.Postgres)
        {
            options.UseNpgsql(settings.ConnectionString)
                .UseSnakeCaseNamingConvention();
            return;
        }

        options.UseSqlite(string.IsNullOrWhiteSpace(settings.ConnectionString)
                ? "Data Source=d_system.db"
                : settings.ConnectionString)
            .UseSnakeCaseNamingConvention();
    }
}
