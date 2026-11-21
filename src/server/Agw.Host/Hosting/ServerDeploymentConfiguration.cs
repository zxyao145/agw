using Agw.Infrastructure.Configuration;
using Agw.Shared.Configuration;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration.Memory;
using Npgsql;

namespace Agw.Host.Hosting;

public static class ServerDeploymentConfiguration
{
    public static void Apply(ConfigurationManager configuration)
    {
        configuration.Sources.Insert(
            0,
            new MemoryConfigurationSource
            {
                InitialData = new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "sqlite",
                    ["Database:ConnectionString"] = "Data Source=agw.db",
                    ["Execution:Provider"] = "InProcess",
                    ["Events:Transport:Provider"] = "InProcess",
                    ["DistributedLock:Provider"] = null,
                    ["DistributedLock:ConnectionString"] = string.Empty,
                },
            }
        );
    }

    public static void Validate(AgwHostProfile profile, IConfiguration configuration)
    {
        var lockProvider = configuration["DistributedLock:Provider"];
        if (
            profile != AgwHostProfile.Standalone
            && (
                !string.Equals(configuration["Database:Provider"], "postgres", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    configuration["Execution:Provider"],
                    "Distributed",
                    StringComparison.OrdinalIgnoreCase
                )
                || (
                    !string.IsNullOrWhiteSpace(lockProvider)
                    && !string.Equals(lockProvider, "postgres", StringComparison.OrdinalIgnoreCase)
                )
            )
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidSetupConfiguration,
                $"The {profile} requires PostgreSQL, Distributed execution, and a PostgreSQL distributed lock."
            );
        }

        ValidateDatabaseConnectionString(configuration);
        ValidateEventTransport(profile, configuration);
    }

    private static void ValidateEventTransport(AgwHostProfile profile, IConfiguration configuration)
    {
        var provider = configuration["Events:Transport:Provider"] ?? "InProcess";
        if (
            !string.Equals(provider, "InProcess", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase)
        )
            throw new AgwException(
                ErrorCodes.InvalidSetupConfiguration,
                "Events:Transport:Provider must be InProcess or Redis."
            );
        if (
            profile != AgwHostProfile.Standalone
            && !string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase)
        )
            throw new AgwException(
                ErrorCodes.InvalidSetupConfiguration,
                "ControlPlane and DataPlane require the Redis event transport."
            );
        if (
            string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(configuration["Events:Transport:Redis:ConnectionString"])
        )
            throw new AgwException(
                ErrorCodes.InvalidSetupConfiguration,
                "Events:Transport:Redis:ConnectionString is required."
            );
    }

    private static void ValidateDatabaseConnectionString(IConfiguration configuration)
    {
        var provider = DatabaseProviderResolver.Parse(configuration["Database:Provider"] ?? "sqlite");
        var connectionString = configuration["Database:ConnectionString"] ?? string.Empty;
        try
        {
            if (provider == DatabaseProvider.Sqlite)
            {
                _ = new SqliteConnectionStringBuilder(connectionString);
                return;
            }

            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrWhiteSpace(builder.Host))
            {
                return;
            }
        }
        catch (ArgumentException)
        {
            // Parser errors can contain credentials. Report only the configuration key below.
        }

        throw new AgwException(
            ErrorCodes.InvalidSetupConfiguration,
            provider == DatabaseProvider.Postgres
                ? "Database:ConnectionString must be a valid PostgreSQL connection string with a nonempty Host. Configure Database:Provider and Database:ConnectionString together."
                : "Database:ConnectionString must be a valid SQLite connection string."
        );
    }
}
