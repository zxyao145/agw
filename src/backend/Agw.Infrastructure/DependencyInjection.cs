using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Infrastructure.Services;
using Agw.Jobs.Application.Services;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Services;

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
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IGitCommandService, GitCommandService>();
        services.AddScoped<IJobStore, JobStore>();

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
