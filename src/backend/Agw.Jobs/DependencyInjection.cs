using Agw.Jobs.Services;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.PostgreSql;
using Hangfire.Storage.SQLite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs;

public static class DependencyInjection
{
    public static IServiceCollection AddJobs(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "sqlite";
        var connectionString = configuration["Database:ConnectionString"] ?? "Data Source=agw.db";

        services.AddHangfire((serviceProvider, globalConfiguration) =>
        {
            globalConfiguration
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings();

            switch (provider.ToLowerInvariant())
            {
                case "postgres":
                case "postgresql":
                    globalConfiguration.UsePostgreSqlStorage(options =>
                        options.UseNpgsqlConnection(connectionString), new PostgreSqlStorageOptions
                        {
                            PrepareSchemaIfNecessary = true
                        });
                    break;

                case "sqlite":
                    globalConfiguration.UseSQLiteStorage(string.IsNullOrWhiteSpace(connectionString)
                        ? "Data Source=agw.db"
                        : connectionString);
                    break;

                default:
                    globalConfiguration.UseInMemoryStorage(new InMemoryStorageOptions());
                    break;
            }
        });

        services.AddHangfireServer();
        services.AddScoped<IHangfireJobAppService, HangfireJobAppService>();
        services.AddScoped<ManagedHangfireJobExecutor>();
        return services;
    }
}
