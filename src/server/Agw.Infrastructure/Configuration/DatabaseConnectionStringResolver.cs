using Microsoft.Data.Sqlite;

using Agw.Shared.Configuration;
using Agw.Shared.Runtime;

namespace Agw.Infrastructure.Configuration;

public static class DatabaseConnectionStringResolver
{
    public static string Resolve(DatabaseProvider provider, string connectionString, AgwDataPaths paths)
    {
        if (provider != DatabaseProvider.Sqlite)
        {
            return connectionString;
        }

        return ResolveSqlite(connectionString, paths);
    }
    private static string ResolveSqlite(string connectionString, AgwDataPaths paths)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            builder.DataSource = paths.DatabaseFile;
        }
        else if (builder.DataSource != ":memory:" && !Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(paths.DatabaseFile)!, builder.DataSource));
        }

        return builder.ToString();
    }
}
