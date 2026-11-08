using Agw.Extensions.Infrastructure.Database;
using Agw.Extensions.Sdk.Database;
using Npgsql;
using Xunit;

namespace Agw.Extensions.Database.Tests;

[Trait("Category", "Postgres")]
public sealed class ExtensionDatabaseLockCoordinatorPostgresTests
{
    [Fact]
    public async Task AcquireBusinessAccessAsync_UsesTransactionSharedLockAgainstMigrationSessionLock()
    {
        string connectionString = ExtensionDatabaseMigrationTests.RequirePostgresConnectionString();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancellationToken = timeout.Token;
        var coordinator = new ExtensionDatabaseLockCoordinator();
        Guid extensionId = Guid.CreateVersion7();

        await using var businessConnection = new NpgsqlConnection(connectionString);
        await businessConnection.OpenAsync(cancellationToken);
        await using var businessTransaction = await businessConnection.BeginTransactionAsync(cancellationToken);
        await using var businessAccess = await coordinator.AcquireBusinessAccessAsync(
            extensionId,
            ExtensionDatabaseProvider.PostgreSql,
            businessConnection,
            businessTransaction,
            cancellationToken
        );

        await using var migrationConnection = new NpgsqlConnection(connectionString);
        await migrationConnection.OpenAsync(cancellationToken);
        Task<IAsyncDisposable> migrationLockTask = coordinator
            .AcquireMigrationAccessAsync(
                extensionId,
                ExtensionDatabaseProvider.PostgreSql,
                migrationConnection,
                cancellationToken
            )
            .AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        bool migrationWaitedForBusiness = !migrationLockTask.IsCompleted;

        await businessTransaction.CommitAsync(cancellationToken);
        await using IAsyncDisposable migrationLock = await migrationLockTask.WaitAsync(
            TimeSpan.FromSeconds(10),
            cancellationToken
        );

        await using var nextBusinessConnection = new NpgsqlConnection(connectionString);
        await nextBusinessConnection.OpenAsync(cancellationToken);
        await using var nextBusinessTransaction = await nextBusinessConnection.BeginTransactionAsync(cancellationToken);
        Task<IAsyncDisposable> nextBusinessLockTask = coordinator
            .AcquireBusinessAccessAsync(
                extensionId,
                ExtensionDatabaseProvider.PostgreSql,
                nextBusinessConnection,
                nextBusinessTransaction,
                cancellationToken
            )
            .AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        bool businessWaitedForMigration = !nextBusinessLockTask.IsCompleted;

        await migrationLock.DisposeAsync();
        await using IAsyncDisposable nextBusinessAccess = await nextBusinessLockTask.WaitAsync(
            TimeSpan.FromSeconds(10),
            cancellationToken
        );
        await nextBusinessTransaction.CommitAsync(cancellationToken);

        Assert.True(migrationWaitedForBusiness);
        Assert.True(businessWaitedForMigration);
    }
}
