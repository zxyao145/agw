using System.Text.Json;

namespace Agw.Tests;

internal static class IsolatedTestConnections
{
    public static string? Postgres => ReadSetting("postgres-tests.json", "ConnectionString");

    public static string? Redis => ReadSetting("redis-tests.json", "ConnectionString");

    public static string? RedisServerExecutable => ReadSetting("redis-tests.json", "ServerExecutable");

    private static string? ReadSetting(string fileName, string key)
    {
        for (
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (!File.Exists(Path.Combine(directory.FullName, "Agw.slnx")))
                continue;

            var path = Path.Combine(directory.FullName, ".scratch", fileName);
            if (!File.Exists(path))
                return null;

            using var settings = JsonDocument.Parse(File.ReadAllText(path));
            var value = settings.RootElement.TryGetProperty(key, out var setting) ? setting.GetString() : null;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }
}
