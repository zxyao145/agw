using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Infrastructure.Services;
using Agw.Jobs.Services;
using Agw.Shared.Abstractions.Repositories;
using Agw.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Infrastructure;

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

        // Register database seeder
        services.AddScoped<DbSeeder>();

        services.AddScoped<DbContext, LlmDbContext>();
        services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IGitCommandService, GitCommandService>();
        services.AddScoped<IJobStore, JobStore>();

        return services;
    }
}
