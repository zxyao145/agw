using Agw.Tests;
using Npgsql;
using Xunit;

namespace Agw.Events.Tests;

internal sealed class PostgresEventTestDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;

    private PostgresEventTestDatabase(string adminConnectionString, string databaseName, string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        HostSettings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "postgres",
            ["Database:ConnectionString"] = connectionString,
            ["Execution:Provider"] = "InProcess",
            ["DistributedLock:Provider"] = "postgres",
            ["DistributedLock:ConnectionString"] = connectionString,
        };
    }

    public IReadOnlyDictionary<string, string?> HostSettings { get; }

    public static async Task<PostgresEventTestDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var connectionString = IsolatedTestConnections.Postgres;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Skip(
                "Set ConnectionString in .scratch/postgres-tests.json to an isolated PostgreSQL server "
                    + "with CREATE DATABASE permission."
            );
        }

        var adminSettings = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var databaseName = $"agw_events_test_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync(cancellationToken);
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
        await create.ExecuteNonQueryAsync(cancellationToken);

        var databaseSettings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString)
        {
            Database = databaseName,
        };
        return new PostgresEventTestDatabase(
            adminSettings.ConnectionString,
            databaseName,
            databaseSettings.ConnectionString
        );
    }

    public async ValueTask DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync(CancellationToken.None);
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
