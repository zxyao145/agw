using Agw.Extensions.Infrastructure.Database;
using Agw.Extensions.Sdk.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Agw.Extensions.Database.Tests;

public sealed class ExtensionDatabaseLockCoordinatorTests
{
    [Fact]
    public async Task AcquireMigrationAccessAsync_ExistingFileOwner_WaitsUntilOwnershipReleased()
    {
        var databasePath = Path.Combine(AppContext.BaseDirectory, $"migration-lock-{Guid.CreateVersion7():N}.db");
        Guid extensionId = Guid.CreateVersion7();
        string lockPath = $"{databasePath}.{extensionId:N}.migration.lock";
        try
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var owner = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );
            var coordinator = new ExtensionDatabaseLockCoordinator();
            var pending = coordinator
                .AcquireMigrationAccessAsync(
                    extensionId,
                    ExtensionDatabaseProvider.Sqlite,
                    connection,
                    TestContext.Current.CancellationToken
                )
                .AsTask();
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            await owner.DisposeAsync();
            await using var acquired = await pending.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken
            );
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(lockPath);
        }
    }

    [Fact]
    public async Task AcquireMigrationAccessAsync_WaitsForBusinessTransaction()
    {
        var coordinator = new ExtensionDatabaseLockCoordinator();
        Guid extensionId = Guid.CreateVersion7();
        await using var businessConnection = new SqliteConnection("Data Source=:memory:");
        await businessConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using var businessTransaction = await businessConnection.BeginTransactionAsync(
            TestContext.Current.CancellationToken
        );
        await using var businessAccess = await coordinator.AcquireBusinessAccessAsync(
            extensionId,
            ExtensionDatabaseProvider.Sqlite,
            businessConnection,
            businessTransaction,
            TestContext.Current.CancellationToken
        );

        await using var migrationConnection = new SqliteConnection("Data Source=:memory:");
        await migrationConnection.OpenAsync(TestContext.Current.CancellationToken);
        Task<IAsyncDisposable> migrationAccess = coordinator
            .AcquireMigrationAccessAsync(
                extensionId,
                ExtensionDatabaseProvider.Sqlite,
                migrationConnection,
                TestContext.Current.CancellationToken
            )
            .AsTask();

        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.False(migrationAccess.IsCompleted);

        await businessTransaction.CommitAsync(TestContext.Current.CancellationToken);
        await businessAccess.DisposeAsync();
        await using IAsyncDisposable acquiredMigrationAccess = await migrationAccess.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken
        );
    }

    [Fact]
    public async Task AcquireMigrationAccessAsync_DoesNotWaitForDifferentExtension()
    {
        var coordinator = new ExtensionDatabaseLockCoordinator();
        await using var businessConnection = new SqliteConnection("Data Source=:memory:");
        await businessConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using var businessTransaction = await businessConnection.BeginTransactionAsync(
            TestContext.Current.CancellationToken
        );
        await using var businessAccess = await coordinator.AcquireBusinessAccessAsync(
            Guid.CreateVersion7(),
            ExtensionDatabaseProvider.Sqlite,
            businessConnection,
            businessTransaction,
            TestContext.Current.CancellationToken
        );

        await using var migrationConnection = new SqliteConnection("Data Source=:memory:");
        await migrationConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using IAsyncDisposable migrationAccess = await coordinator
            .AcquireMigrationAccessAsync(
                Guid.CreateVersion7(),
                ExtensionDatabaseProvider.Sqlite,
                migrationConnection,
                TestContext.Current.CancellationToken
            )
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
