using Agw.Extensions.TestSupport;
using Agw.Shared.Configuration;
using Agw.Tests;
using Npgsql;
using Xunit;

namespace Agw.Extensions.TaskPilot.Tests;

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
}
