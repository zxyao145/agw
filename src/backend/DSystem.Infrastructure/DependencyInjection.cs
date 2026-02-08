using DSystem.Infrastructure.Configuration;
using DSystem.Infrastructure.Data;
using DSystem.Infrastructure.Repositories;
using DSystem.Domain.Repositories;
using DSystem.SessionRecords.Repositories;
using DSystem.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DSystem.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = new DatabaseSettings();
        configuration.GetSection(DatabaseSettings.SectionName).Bind(settings);

        services.Configure<DatabaseSettings>(configuration.GetSection(DatabaseSettings.SectionName));
        services.AddDbContext<LlmDbContext>(options =>
        {
            switch (settings.Provider.ToLowerInvariant())
            {
                case "postgres":
                case "postgresql":
                    options.UseNpgsql(settings.ConnectionString)
                        .UseSnakeCaseNamingConvention();
                    break;
                default:
                    options.UseSqlite(string.IsNullOrWhiteSpace(settings.ConnectionString)
                        ? "Data Source=d_system.db"
                        : settings.ConnectionString)
                        .UseSnakeCaseNamingConvention();
                    break;
            }
            options.ReplaceService<IMigrationsModelDiffer, NoForeignKeyModelDiffer>();
        });

        services.AddScoped<DbContext, LlmDbContext>();
        services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAgentSessionRecordRepository, AgentSessionRecordRepository>();
        services.AddScoped<ISessionRecordsUnitOfWork, SessionRecordsUnitOfWork>();
        services.AddScoped<IGitCommandService, GitCommandService>();

        return services;
    }
}

