using Microsoft.Data.Sqlite;

using Agw.Shared.Runtime;

namespace Agw.Infrastructure.Configuration;

public static class DatabaseConnectionStringResolver
{
    public static string Resolve(string? provider, string connectionString, AgwDataPaths paths)
    {
        if (!string.Equals(provider?.Trim(), "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

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
