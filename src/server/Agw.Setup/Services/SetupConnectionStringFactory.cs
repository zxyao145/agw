using Agw.Infrastructure.Configuration;
using Agw.Setup.Contracts;
using Agw.Shared.Configuration;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;

using Microsoft.Data.Sqlite;

using Npgsql;

namespace Agw.Setup.Services;

public static class SetupConnectionStringFactory
{
    public static string Create(SetupRequest request, AgwDataPaths paths)
    {
        return request.Provider switch
        {
            DatabaseProvider.Sqlite => CreateSqlite(request, paths),
            DatabaseProvider.Postgres => CreatePostgres(request),
            _ => throw new AgwException(ErrorCodes.UnsupportedDatabaseProvider)
        };
    }

    private static string CreateSqlite(SetupRequest request, AgwDataPaths paths)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = request.SqlitePath.Trim()
        };
        return DatabaseConnectionStringResolver.Resolve(
            DatabaseProvider.Sqlite,
            builder.ConnectionString,
            paths);
    }

    private static string CreatePostgres(SetupRequest request)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = request.PostgresHost.Trim(),
            Port = request.PostgresPort,
            Database = request.PostgresDatabase.Trim(),
            Username = request.PostgresUsername.Trim(),
            Password = request.PostgresPassword,
            ApplicationName = "agw-server"
        };
        return builder.ConnectionString;
    }
}
