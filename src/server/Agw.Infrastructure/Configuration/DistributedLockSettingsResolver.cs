using Agw.Shared.Configuration;
using Agw.Shared.Exceptions;

namespace Agw.Infrastructure.Configuration;

public static class DistributedLockSettingsResolver
{
    public static DistributedLockProvider ParseProvider(string provider)
    {
        return provider.Trim().ToLowerInvariant() switch
        {
            "inmemory" => DistributedLockProvider.InMemory,
            "postgres" => DistributedLockProvider.Postgres,
            _ => throw new AgwException(
                ErrorCodes.UnsupportedDistributedLockProvider,
                $"Distributed lock provider '{provider}' is not supported."
            ),
        };
    }

    public static DistributedLockSettings Resolve(
        DistributedLockSettings? settings,
        DatabaseProvider databaseProvider,
        string databaseConnectionString
    )
    {
        var provider = settings?.Provider ?? ResolveFromDatabase(databaseProvider);

        Validate(provider);

        return new DistributedLockSettings
        {
            Provider = provider,
            ConnectionString =
                provider == DistributedLockProvider.Postgres
                    ? ResolveConnectionString(settings?.ConnectionString, databaseConnectionString)
                    : string.Empty,
        };
    }

    private static DistributedLockProvider ResolveFromDatabase(DatabaseProvider databaseProvider)
    {
        return databaseProvider switch
        {
            DatabaseProvider.Sqlite => DistributedLockProvider.InMemory,
            DatabaseProvider.Postgres => DistributedLockProvider.Postgres,
            _ => throw new AgwException(ErrorCodes.UnsupportedDatabaseProvider),
        };
    }

    private static void Validate(DistributedLockProvider provider)
    {
        if (provider is not DistributedLockProvider.InMemory and not DistributedLockProvider.Postgres)
        {
            throw new AgwException(
                ErrorCodes.UnsupportedDistributedLockProvider,
                $"Distributed lock provider '{provider}' is not supported."
            );
        }
    }

    private static string ResolveConnectionString(string? connectionString, string databaseConnectionString)
    {
        return string.IsNullOrWhiteSpace(connectionString) ? databaseConnectionString : connectionString;
    }
}
