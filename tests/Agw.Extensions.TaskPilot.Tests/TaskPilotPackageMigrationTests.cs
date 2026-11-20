using System.Data.Common;
using Agw.Extensions.TestSupport;
using Agw.Shared.Configuration;
using Agw.Tests;
using Npgsql;
using Xunit;

namespace Agw.Extensions.TaskPilot.Tests;

[Collection("TaskPilot extension package")]
public sealed class TaskPilotPackageMigrationTests
{
    private static readonly Guid ExtensionId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48a101");

    public static bool PostgresConfigured => IsolatedTestConnections.Postgres is not null;

    [Fact]
    public async Task LoadAndActivate_SignedPackageAppliesSqliteMigrations()
    {
        string packageDirectory = GetPackageDirectory();
        await using ExtensionRuntimeIntegrationHost host = await ExtensionRuntimeIntegrationHost.CreateAsync(
            packageDirectory,
            cancellationToken: TestContext.Current.CancellationToken
        );

        await host.LoadAndActivateAsync(ExtensionId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{ExtensionId:N}_rule"));
        Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{ExtensionId:N}_delivery"));
        Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{ExtensionId:N}___outbox"));
        Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{ExtensionId:N}___inbox"));
    }

    [Fact]
    public async Task Upgrade_ResetEventSubscriptions_ClearsSubscriptionState()
    {
        await using var host = await ExtensionRuntimeIntegrationHost.CreateAsync(
            GetPackageDirectory(),
            cancellationToken: TestContext.Current.CancellationToken
        );
        await host.LoadAndActivateAsync(ExtensionId, cancellationToken: TestContext.Current.CancellationToken);
        await VerifyResetEventSubscriptionsAsync(host, "TaskPilotSqliteResetEventSubscriptions", "0");
    }

    private static async Task VerifyResetEventSubscriptionsAsync(
        ExtensionRuntimeIntegrationHost host,
        string migrationName,
        string createTimeLiteral
    )
    {
        await host.StopGenerationAsync(ExtensionId, TestContext.Current.CancellationToken);

        var prefix = $"zext_{ExtensionId:N}_";
        var subscriptionId = Guid.CreateVersion7();
        var outboxId = Guid.CreateVersion7();
        await ExecuteAsync(
            host.DatabaseConnection,
            $"""
            INSERT INTO "{prefix}user_state"
                (id, system_subscription_version, system_subscription_id, system_subscription_outbox_id,
                 system_subscription_error, create_by, create_time)
            VALUES ('{Guid.CreateVersion7():D}', 1, '{subscriptionId:D}', '{outboxId:D}',
                    'previous error', 'owner', {createTimeLiteral})
            """
        );
        await ExecuteAsync(
            host.DatabaseConnection,
            $"""
            INSERT INTO "{prefix}rule"
                (id, name, kind, enabled, paused, version, subscription_id, subscription_outbox_id,
                 subscription_error, source_ids_json, condition_json, event_types_json, target_kind,
                 project_id, instructions, data_scope_json, permission_mode, ask_first_json, group_key,
                 max_concurrency, session_policy, channels_json, notify_policy, emit_types_json, retry_json,
                 delivery_json, create_by, create_time)
            VALUES ('{Guid.CreateVersion7():D}', 'existing rule', 'Agent', TRUE, FALSE, 1, '{subscriptionId:D}',
                    '{outboxId:D}', 'previous error', '[]', '[]', '[]', 'Agent', '{Guid.Empty:D}', '',
                    '[]', 'Default', '[]', 'rule:existing', 1, 'PerMatter', '[]', 'Never', '[]',
                    '[]', '[]', 'owner', {createTimeLiteral})
            """
        );
        await ExecuteAsync(
            host.DatabaseConnection,
            $"DELETE FROM \"{prefix}__ef_migrations_history\" WHERE migration_id LIKE '%_{migrationName}'"
        );

        await host.LoadAndActivateAsync(
            ExtensionId,
            epoch: 1,
            resumeBackground: false,
            cancellationToken: TestContext.Current.CancellationToken
        );

        await using var userState = host.DatabaseConnection.CreateCommand();
        userState.CommandText =
            $"SELECT system_subscription_id, system_subscription_outbox_id, system_subscription_error FROM \"{prefix}user_state\"";
        await using (var reader = await userState.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(2));
        }

        await using var rule = host.DatabaseConnection.CreateCommand();
        rule.CommandText = $"SELECT subscription_id, subscription_outbox_id, subscription_error FROM \"{prefix}rule\"";
        await using (var reader = await rule.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal("Please save the rule again.", reader.GetString(2));
        }
    }

    [Fact(
        SkipUnless = nameof(PostgresConfigured),
        Skip = "Set ConnectionString in .scratch/postgres-tests.json to an isolated PostgreSQL server with CREATE DATABASE permission."
    )]
    public async Task LoadAndActivate_SignedPackageAppliesPostgresMigrations()
    {
        string configuredConnectionString = IsolatedTestConnections.Postgres!;
        var configured = new NpgsqlConnectionStringBuilder(configuredConnectionString);
        string databaseName = $"taskpilot_runtime_{Guid.NewGuid():N}";
        var adminSettings = new NpgsqlConnectionStringBuilder(configuredConnectionString) { Database = "postgres" };

        await using var adminConnection = new NpgsqlConnection(adminSettings.ConnectionString);
        await adminConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = adminConnection.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            configured.Database = databaseName;
            await using ExtensionRuntimeIntegrationHost host = await ExtensionRuntimeIntegrationHost.CreateAsync(
                GetPackageDirectory(),
                databaseProvider: DatabaseProvider.Postgres,
                databaseConnectionString: configured.ConnectionString,
                cancellationToken: TestContext.Current.CancellationToken
            );
            await host.LoadAndActivateAsync(ExtensionId, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{ExtensionId:N}_rule"));
            Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{ExtensionId:N}_delivery"));
            Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{ExtensionId:N}___outbox"));
            Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{ExtensionId:N}___inbox"));
            await VerifyResetEventSubscriptionsAsync(
                host,
                "TaskPilotPostgresResetEventSubscriptions",
                "TIMESTAMPTZ '1970-01-01 00:00:00+00'"
            );
        }
        finally
        {
            await using var drop = adminConnection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static string GetPackageDirectory()
    {
        string directory = Path.Combine(FindRepositoryRoot(), "src", "extensions", "taskpilot", "dist");
        Assert.True(
            File.Exists(Path.Combine(directory, "manifest.json")),
            "Build the TaskPilot package before running this test."
        );
        return directory;
    }

    private static string FindRepositoryRoot()
    {
        for (
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (File.Exists(Path.Combine(directory.FullName, "Agw.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private static async Task<bool> HasTableAsync(System.Data.Common.DbConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        if (connection is NpgsqlConnection)
        {
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = @name)";
        }
        else
        {
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name)";
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = "name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
