using System.Data.Common;
using Agw.Extensions.TestSupport;
using Agw.Shared.Configuration;
using Agw.Tests;
using Npgsql;
using Xunit;

namespace Agw.Extensions.Feishu.Tests;

public sealed class FeishuPackageMigrationTests
{
    public static bool PostgresConfigured => IsolatedTestConnections.Postgres is not null;

    [Fact]
    public async Task LoadAndActivate_SignedPackageAppliesSqliteMigrations()
    {
        await using ExtensionRuntimeIntegrationHost host = await ExtensionRuntimeIntegrationHost.CreateAsync(
            GetPackageDirectory(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        await host.LoadAndActivateAsync(
            FeishuExtension.ExtensionId,
            resumeBackground: false,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{FeishuExtension.ExtensionId:N}_app"));
        Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{FeishuExtension.ExtensionId:N}_target"));
        Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{FeishuExtension.ExtensionId:N}___outbox"));
        Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{FeishuExtension.ExtensionId:N}___inbox"));
    }

    [Fact(
        SkipUnless = nameof(PostgresConfigured),
        Skip = "Set ConnectionString in .scratch/postgres-tests.json to an isolated PostgreSQL server with CREATE DATABASE permission."
    )]
    public async Task LoadAndActivate_SignedPackageAppliesPostgresMigrations()
    {
        string configuredConnectionString = IsolatedTestConnections.Postgres!;
        var configured = new NpgsqlConnectionStringBuilder(configuredConnectionString);
        string databaseName = $"feishu_runtime_{Guid.NewGuid():N}";
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
            await host.LoadAndActivateAsync(
                FeishuExtension.ExtensionId,
                resumeBackground: false,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{FeishuExtension.ExtensionId:N}_app"));
            Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{FeishuExtension.ExtensionId:N}_target"));
            Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{FeishuExtension.ExtensionId:N}___outbox"));
            Assert.True(await HasTableAsync(host.DatabaseConnection, $"zext_{FeishuExtension.ExtensionId:N}___inbox"));
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
        string directory = Path.Combine(FindRepositoryRoot(), "src", "extensions", "feishu", "dist");
        Assert.True(
            File.Exists(Path.Combine(directory, "manifest.json")),
            "Build the Feishu package before running this test."
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
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private static async Task<bool> HasTableAsync(DbConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            connection is NpgsqlConnection
                ? "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = @name)"
                : "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
