using System.Data.Common;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;
using Microsoft.Data.Sqlite;

namespace Agw.Infrastructure.Configuration;

public static class DatabaseConnectionStringResolver
{
    private const string RedactedValue = "***";

    public static string Resolve(DatabaseProvider provider, string connectionString, AgwDataPaths paths)
    {
        if (provider != DatabaseProvider.Sqlite)
        {
            return connectionString;
        }

        return ResolveSqlite(connectionString, paths);
    }

    public static string ToSafeLogValue(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return string.Empty;
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var sensitiveKeys = builder.Keys.Cast<string>().Where(IsSensitiveKey).ToArray();
            foreach (var key in sensitiveKeys)
            {
                builder[key] = RedactedValue;
            }

            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            return "<invalid connection string>";
        }
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
            builder.DataSource = Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(paths.DatabaseFile)!, builder.DataSource)
            );
        }

        return builder.ToString();
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalizedKey = string.Concat(key.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return normalizedKey
            is "password"
                or "pwd"
                or "passfile"
                or "sslpassword"
                or "token"
                or "accesstoken"
                or "apikey"
                or "clientsecret"
                or "accountkey"
                or "sharedaccesskey";
    }
}
