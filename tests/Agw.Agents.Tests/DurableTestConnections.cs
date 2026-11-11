using Agw.Tests;

namespace Agw.Agents.Tests;

internal static class DurableTestConnections
{
    public static string? PostgresConnectionString =>
        ReadExistingOverride("AGW_TEST_POSTGRES_CONNECTION_STRING") ?? IsolatedTestConnections.Postgres;

    public static string? RedisConnectionString =>
        ReadExistingOverride("AGW_TEST_REDIS_CONNECTION_STRING") ?? IsolatedTestConnections.Redis;

    private static string? ReadExistingOverride(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
