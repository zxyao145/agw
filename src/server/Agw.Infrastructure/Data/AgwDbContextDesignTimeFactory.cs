using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data.Encryption;
using Agw.Shared.Configuration;
using Agw.Shared.Exceptions;
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
        var settings =
            configuration.GetSection(DatabaseSettings.SectionName).Get<DatabaseSettings>() ?? new DatabaseSettings();
        var configuredProvider = configuration[$"{DatabaseSettings.SectionName}:Provider"];
        if (!string.IsNullOrWhiteSpace(configuredProvider))
        {
            settings.Provider = DatabaseProviderResolver.Parse(configuredProvider);
        }

        var providerArgument = GetArgumentValue(args, "--provider");
        var provider = string.IsNullOrWhiteSpace(providerArgument)
            ? settings.Provider
            : DatabaseProviderResolver.Parse(providerArgument);
        var connectionString = provider == settings.Provider ? settings.ConnectionString : string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = GetDesignTimeConnectionString(provider);
        }

        var options = new DbContextOptionsBuilder<AgwDbContext>();
        AgwDbContextOptionsConfigurator.Configure(options, provider, connectionString);

        var dataPaths = AgwDataPaths.ResolveFromEnvironment();
        dataPaths.EnsureCreated();
        var dataProtectionProvider = AgwDataProtectionConfiguration.CreatePersistedProvider(
            new DirectoryInfo(dataPaths.KeysDirectory)
        );
        var encryptedDataProtector = new DataProtectionEncryptedDataProtector(dataProtectionProvider);

        return new AgwDbContext(options.Options, encryptedDataProtector);
    }

    private static IConfiguration BuildConfiguration()
    {
        var hostDirectory = FindHostDirectory();
        var environment =
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
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

    private static string GetDesignTimeConnectionString(DatabaseProvider provider)
    {
        return provider == DatabaseProvider.Postgres
            ? "Host=localhost;Database=agw;Username=postgres"
            : "Data Source=agw.db";
    }

    private static string? GetArgumentValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith($"{name}=", StringComparison.OrdinalIgnoreCase))
            {
                return argument[(name.Length + 1)..];
            }

            if (!string.Equals(argument, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new AgwException(ErrorCodes.InvalidParam, $"{name} requires a value.");
            }

            return args[index + 1];
        }

        return null;
    }
}
