using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Jobs;
using Agw.Infrastructure.Repositories;
using Agw.Integrations.Domain.Entities;
using Agw.Jobs.Application.Services;
using Agw.Jobs.Domain.Entities;
using Agw.Jobs.External;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace Agw.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DatabaseSettings>(configuration.GetSection(DatabaseSettings.SectionName));
        services.AddDbContext<AgwDbContext>((serviceProvider, options) =>
        {
            var settings = serviceProvider
                .GetRequiredService<IOptionsMonitor<DatabaseSettings>>()
                .CurrentValue;

            ConfigureDatabaseProvider(options, settings);
            options.ReplaceService<IMigrationsModelDiffer, NoForeignKeyModelDiffer>();
        });

        // Register database seeder
        services.AddScoped<DbSeeder>();

        services.AddScoped<DbContext, AgwDbContext>();
        services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
        services.AddScoped<IRepository<AppDefinition>, AppDefinitionRepo>();
        services.AddScoped<JobRepo>();
        services.AddScoped<IRepository<Job>, JobRepo>(sp => sp.GetRequiredService<JobRepo>());
        services.AddScoped<IJobStore, JobRepo>(sp => sp.GetRequiredService<JobRepo>());
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddSingleton<IGitCommandService, GitCommandService>();

        // Redis
        var redisConnectionString = configuration.GetValue<string>("Redis:ConnectionString") ?? "localhost:6379,abortConnect=false";
        var redisConfiguration = ConfigurationOptions.Parse(redisConnectionString);
        redisConfiguration.AbortOnConnectFail = false;
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConfiguration));
        services.AddSingleton<IProjectExecutionLock, RedisProjectExecutionLock>();

        return services;
    }

    private static void ConfigureDatabaseProvider(DbContextOptionsBuilder options, DatabaseSettings settings)
    {
        var provider = settings.Provider?.Trim().ToLowerInvariant();

        switch (provider)
        {
            case "postgres":
            case "postgresql":
                options.UseNpgsql(settings.ConnectionString)
                    .UseSnakeCaseNamingConvention();
                break;

            case "mysql":
                options.UseMySql(
                        settings.ConnectionString,
                        ServerVersion.AutoDetect(settings.ConnectionString))
                    .UseSnakeCaseNamingConvention();
                break;

            default:
                options.UseSqlite(string.IsNullOrWhiteSpace(settings.ConnectionString)
                        ? "Data Source=d_system.db"
                        : settings.ConnectionString)
                    .UseSnakeCaseNamingConvention();
                break;
        }
    }
}
