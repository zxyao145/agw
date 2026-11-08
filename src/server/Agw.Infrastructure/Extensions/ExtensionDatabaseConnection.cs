using Agw.Extensions.Sdk.Database;
using Agw.Infrastructure.Configuration;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Extensions;

public sealed class ExtensionDatabaseConnection : IExtensionDatabaseConnection
{
    public ExtensionDatabaseConnection(IOptions<DatabaseSettings> settings, AgwDataPaths paths)
    {
        var database = settings.Value;
        Provider =
            database.Provider == DatabaseProvider.Sqlite
                ? ExtensionDatabaseProvider.Sqlite
                : ExtensionDatabaseProvider.PostgreSql;
        ConnectionString = DatabaseConnectionStringResolver.Resolve(
            database.Provider,
            database.ConnectionString,
            paths
        );
    }

    public ExtensionDatabaseProvider Provider { get; }
    public string ConnectionString { get; }
}
