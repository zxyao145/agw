using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data.Encryption;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Agw.Infrastructure.Data;

public class AgwDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AgwDbContext>
{
    public AgwDbContext CreateDbContext(string[] args)
    {
        var configuration = BuildConfiguration();
        var configuredProvider = configuration[$"{DatabaseSettings.SectionName}:Provider"];
        if (configuredProvider != null)
        {
            DatabaseProviderResolver.Parse(configuredProvider);
        }

        var settings = configuration
            .GetSection(DatabaseSettings.SectionName)
            .Get<DatabaseSettings>() ?? new DatabaseSettings();

        var options = new DbContextOptionsBuilder<AgwDbContext>();
        ConfigureDatabaseProvider(options, settings);

        var dataPaths = AgwDataPaths.ResolveFromEnvironment();
        dataPaths.EnsureCreated();
        var dataProtectionProvider = AgwDataProtectionConfiguration.CreatePersistedProvider(
            new DirectoryInfo(dataPaths.KeysDirectory));
        var encryptedDataProtector = new DataProtectionEncryptedDataProtector(dataProtectionProvider);

        return new AgwDbContext(options.Options, encryptedDataProtector);
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
            .AddEnvironmentVariables()
            .Build();
    }

    private static string? FindHostDirectory()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null)
        {
            var hostDirectory = Path.Combine(directory.FullName, "src", "server", "Agw.Host");
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
