using Agw.Shared.Configuration;
using Agw.Shared.Exceptions;

namespace Agw.Infrastructure.Configuration;

public static class DatabaseProviderResolver
{
    public static DatabaseProvider Parse(string provider)
    {
        return provider.Trim().ToLowerInvariant() switch
        {
            "sqlite" => DatabaseProvider.Sqlite,
            "postgres" => DatabaseProvider.Postgres,
            _ => throw new AgwException(
                ErrorCodes.UnsupportedDatabaseProvider,
                $"Database provider '{provider}' is not supported. Supported providers: sqlite, postgres.")
        };
    }
}
