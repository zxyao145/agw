using Agw.Infrastructure.Configuration;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Agw.Infrastructure.Data;

public class AgwDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AgwDbContext>
{
    public AgwDbContext CreateDbContext(string[] args)
    {
        var configuration = BuildConfiguration();
        var settings = configuration
            .GetSection(DatabaseSettings.SectionName)
            .Get<DatabaseSettings>() ?? new DatabaseSettings();

        var options = new DbContextOptionsBuilder<AgwDbContext>();
        ConfigureDatabaseProvider(options, settings);

        return new AgwDbContext(options.Options);
    }

    private static IConfiguration BuildConfiguration()
    {
        var hostDirectory = FindHostDirectory();
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Development";

        return new ConfigurationBuilder()
            .SetBasePath(hostDirectory ?? Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddJsonFile("appsettings.setup.json", optional: true)
            .Build();
    }

    private static string? FindHostDirectory()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null)
        {
            var hostDirectory = Path.Combine(directory.FullName, "src", "backend", "Agw.Host");
            if (File.Exists(Path.Combine(hostDirectory, "appsettings.json")))
            {
                return hostDirectory;
            }

            var siblingHostDirectory = Path.Combine(directory.FullName, "..", "Agw.Host");
            if (File.Exists(Path.Combine(siblingHostDirectory, "appsettings.json")))
            {
                return Path.GetFullPath(siblingHostDirectory);
            }

            directory = directory.Parent;
        }

        return null;
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
