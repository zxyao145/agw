using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Exceptions;
using Agw.Tests;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agw.Extensions.Database.Tests;

public sealed class ExtensionOutboxRecoveryTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task OutboxDispatcher_ResultCommitFails_BacksOffAndProcessesOtherOwners()
    {
        await using PostgresFixture fixture = await CreatePostgresFixtureAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Host.LoadAndActivateAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(fixture.ExtensionConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var constraint = connection.CreateCommand();
        constraint.CommandText = $"""
            ALTER TABLE {OutboxTableName()} ADD CONSTRAINT result_write_check
            CHECK (create_by <> 'outbox-blocked-user' OR state = 'Pending')
            """;
        await constraint.ExecuteNonQueryAsync(cancellationToken);

        JsonElement? blocked = await fixture.Host.InvokeEndpointAsync(
            "outbox-blocked-user",
            "enqueue-outbox",
            body: null,
            cancellationToken
        );
        JsonElement? following = await fixture.Host.InvokeEndpointAsync(
            "outbox-following-user",
            "enqueue-outbox",
            body: null,
            cancellationToken
        );
        Assert.NotNull(blocked);
        Assert.NotNull(following);
        Guid blockedId = blocked.Value.GetProperty("id").GetGuid();
        Guid followingId = following.Value.GetProperty("id").GetGuid();

        var followingResult = await WaitForOutboxResultAsync(
            fixture.ExtensionConnectionString,
            followingId,
            TimeSpan.FromSeconds(12),
            cancellationToken
        );
        Assert.Equal("Completed", followingResult?.State);
        var deferred = await ReadOutboxSnapshotAsync(fixture.ExtensionConnectionString, blockedId, cancellationToken);
        Assert.NotNull(deferred);
        Assert.Equal("Pending", deferred.State);
        Assert.True(deferred.Attempt > 0);
        Assert.NotNull(deferred.NextAttemptAt);

        constraint.CommandText = $"ALTER TABLE {OutboxTableName()} DROP CONSTRAINT result_write_check";
        await constraint.ExecuteNonQueryAsync(cancellationToken);
        var recovered = await WaitForOutboxResultAsync(
            fixture.ExtensionConnectionString,
            blockedId,
            TimeSpan.FromSeconds(20),
            cancellationToken
        );
        Assert.NotNull(recovered);
        Assert.Equal("Completed", recovered.State);
        Assert.True(recovered.Attempt > deferred.Attempt);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task OutboxDispatcher_LostPostgresLease_ReacquiresWithoutReload()
    {
        await using PostgresFixture fixture = await CreatePostgresFixtureAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ExtensionGenerationSnapshot generation = await fixture.Host.LoadAndActivateAsync(cancellationToken);
        ExtensionExecutionPin pin = new(generation.GenerationId.ExtensionId, generation.ReleaseHash, generation.Epoch);
        Assert.True(
            await fixture
                .Host.Services.GetRequiredService<IExtensionRuntimeAvailability>()
                .CanAcceptNewWorkAsync(pin, cancellationToken)
        );
        Assert.Equal(ExtensionGenerationState.Active, Assert.Single(fixture.Host.Runtime.GetSnapshots()).State);

        PostgresLockBackend? originalLease = await WaitForLockBackendAsync(
            fixture.LockConnectionString,
            fixture.LockApplicationName,
            granted: true,
            previous: null,
            TimeSpan.FromSeconds(20),
            cancellationToken
        );
        Assert.NotNull(originalLease);

        await TerminateBackendAsync(fixture.LockConnectionString, originalLease!.ProcessId, cancellationToken);

        PostgresLockBackend? recoveredLease = await WaitForLockBackendAsync(
            fixture.LockConnectionString,
            fixture.LockApplicationName,
            granted: true,
            originalLease,
            TimeSpan.FromSeconds(20),
            cancellationToken
        );
        Assert.NotNull(recoveredLease);
        Assert.True(
            recoveredLease!.BackendStartedAt > originalLease.BackendStartedAt,
            "The dispatcher should acquire a new PostgreSQL lease after its connection is terminated."
        );
        Assert.Equal(generation.GenerationId, Assert.Single(fixture.Host.Runtime.GetSnapshots()).GenerationId);

        JsonElement? response = await fixture.Host.InvokeEndpointAsync(
            "outbox-recovery-user",
            "enqueue-outbox",
            body: null,
            cancellationToken
        );
        Assert.NotNull(response);
        Guid outboxId = response.Value.GetProperty("id").GetGuid();
        OutboxSnapshot? pending = await ReadOutboxSnapshotAsync(
            fixture.ExtensionConnectionString,
            outboxId,
            cancellationToken
        );
        Assert.Equal("outbox-recovery-user", pending?.CreateBy);
        Assert.Equal("Pending", pending?.State);
        Assert.Equal(0, pending?.Attempt);

        OutboxSnapshot? result = await WaitForOutboxResultAsync(
            fixture.ExtensionConnectionString,
            outboxId,
            TimeSpan.FromSeconds(20),
            cancellationToken
        );
        Assert.True(
            result is { State: "Completed" or "Rejected" },
            $"The recovered dispatcher did not finish the pending row. State: {result?.State ?? "missing"}; attempt: {result?.Attempt}."
        );
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task DrainAsync_PendingPostgresOutboxLeaseAcquisition_CancelsThePendingAcquire()
    {
        await using PostgresFixture fixture = await CreatePostgresFixtureAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IApplicationLock applicationLock = fixture.Host.Services.GetRequiredService<IApplicationLock>();
        await using IApplicationLockLease heldLease = await applicationLock.AcquireAsync(
            $"ext:{ExtensionDatabaseTestHost.ExtensionId:D}:outbox",
            cancellationToken
        );

        ExtensionGenerationSnapshot generation = await fixture.Host.LoadAndActivateAsync(cancellationToken);
        PostgresLockBackend? pendingAcquire = await WaitForLockBackendAsync(
            fixture.LockConnectionString,
            fixture.LockApplicationName,
            granted: false,
            previous: null,
            TimeSpan.FromSeconds(20),
            cancellationToken
        );
        Assert.NotNull(pendingAcquire);

        bool drained = await fixture.Host.Runtime.DrainAsync(
            generation.GenerationId,
            TimeSpan.FromSeconds(10),
            cancellationToken
        );
        Assert.True(drained);

        PostgresLockBackend? remainingAcquire = await WaitForLockBackendAsync(
            fixture.LockConnectionString,
            fixture.LockApplicationName,
            granted: false,
            previous: null,
            TimeSpan.FromSeconds(2),
            cancellationToken
        );
        Assert.Null(remainingAcquire);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task OwnedRecordScheduler_UserScopedInvocationCannotReadAnotherUsersRecord()
    {
        await using PostgresFixture fixture = await CreatePostgresFixtureAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Host.LoadAndActivateAsync(cancellationToken);
        Guid aliceRecordId = Guid.CreateVersion7();
        Guid bobRecordId = Guid.CreateVersion7();

        JsonElement? aliceCreated = await fixture.Host.InvokeEndpointAsync(
            "owner-alice",
            "owned-record-create",
            new { id = aliceRecordId, value = "alice" },
            cancellationToken
        );
        JsonElement? bobCreated = await fixture.Host.InvokeEndpointAsync(
            "owner-bob",
            "owned-record-create",
            new { id = bobRecordId, value = "bob" },
            cancellationToken
        );
        Assert.Equal("owner-alice", aliceCreated?.GetProperty("createBy").GetString());
        Assert.Equal("owner-bob", bobCreated?.GetProperty("createBy").GetString());

        JsonElement? aliceRecord = await fixture.Host.InvokeEndpointAsync(
            "owner-alice",
            "owned-record-read",
            new { id = aliceRecordId },
            cancellationToken
        );
        Assert.Equal("owner-alice", aliceRecord?.GetProperty("createBy").GetString());

        AgwException forbidden = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Host.InvokeEndpointAsync(
                "owner-alice",
                "owned-record-read",
                new { id = bobRecordId },
                cancellationToken
            )
        );
        Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, forbidden.Code);

        JsonElement? bobRecord = await fixture.Host.InvokeEndpointAsync(
            "owner-bob",
            "owned-record-read",
            new { id = bobRecordId },
            cancellationToken
        );
        Assert.Equal("owner-bob", bobRecord?.GetProperty("createBy").GetString());

        JsonElement? anonymousBobRecord = await fixture.Host.InvokeEndpointAsync(
            null,
            "owned-record-read",
            new { id = bobRecordId },
            cancellationToken
        );
        Assert.Equal("owner-bob", anonymousBobRecord?.GetProperty("createBy").GetString());
    }

    private static async Task<PostgresFixture> CreatePostgresFixtureAsync()
    {
        string uniqueName = Guid.CreateVersion7().ToString("N");
        string applicationName = $"agw-outbox-{uniqueName}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(ReadPostgresConnectionString())
        {
            ApplicationName = $"{applicationName}-admin",
            Pooling = false,
        };
        ExtensionDatabaseMigrationTests.MigrationTestDatabase database =
            await ExtensionDatabaseMigrationTests.MigrationTestDatabase.CreateAsync(
                ExtensionDatabaseProvider.PostgreSql,
                adminConnectionString.ConnectionString
            );

        try
        {
            string schema = $"agw_outbox_{uniqueName}";
            var bootstrapConnectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                ApplicationName = $"{applicationName}-schema",
            };
            await using (var connection = new NpgsqlConnection(bootstrapConnectionString.ConnectionString))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE SCHEMA {new NpgsqlCommandBuilder().QuoteIdentifier(schema)}";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var extensionConnectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                ApplicationName = $"{applicationName}-extension",
                SearchPath = schema,
            };
            var lockConnectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                ApplicationName = $"{applicationName}-lease",
                SearchPath = schema,
            };
            ExtensionDatabaseTestHost host = await ExtensionDatabaseTestHost.CreateAsync(
                database,
                hostRole: "ControlPlane",
                connectionString: extensionConnectionString.ConnectionString,
                applicationLockConnectionString: lockConnectionString.ConnectionString
            );

            return new PostgresFixture(
                database,
                host,
                extensionConnectionString.ConnectionString,
                lockConnectionString.ConnectionString,
                lockConnectionString.ApplicationName
            );
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<PostgresLockBackend?> WaitForLockBackendAsync(
        string connectionString,
        string applicationName,
        bool granted,
        PostgresLockBackend? previous,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token
        );
        try
        {
            while (true)
            {
                PostgresLockBackend? backend = await FindLockBackendAsync(
                    connectionString,
                    applicationName,
                    granted,
                    previous,
                    linkedCancellation.Token
                );
                if (backend is not null)
                {
                    return backend;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), linkedCancellation.Token);
            }
        }
        catch (OperationCanceledException)
            when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<PostgresLockBackend?> FindLockBackendAsync(
        string connectionString,
        string applicationName,
        bool granted,
        PostgresLockBackend? previous,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT activity.pid, activity.backend_start
            FROM pg_stat_activity AS activity
            JOIN pg_locks AS locks ON locks.pid = activity.pid
            WHERE activity.datname = current_database()
              AND activity.application_name = @application_name
              AND locks.locktype = 'advisory'
              AND locks.granted = @granted
              AND (activity.pid <> @previous_pid OR activity.backend_start <> @previous_start)
            ORDER BY activity.backend_start DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("application_name", applicationName);
        command.Parameters.AddWithValue("granted", granted);
        command.Parameters.AddWithValue("previous_pid", previous?.ProcessId ?? -1);
        command.Parameters.AddWithValue(
            "previous_start",
            NpgsqlDbType.TimestampTz,
            previous?.BackendStartedAt ?? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)
        );

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PostgresLockBackend(reader.GetInt32(0), reader.GetDateTime(1))
            : null;
    }

    private static async Task TerminateBackendAsync(
        string connectionString,
        int processId,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_terminate_backend(@process_id)";
        command.Parameters.AddWithValue("process_id", processId);
        bool terminated = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        Assert.True(terminated);
    }

    private static async Task<OutboxSnapshot?> WaitForOutboxResultAsync(
        string connectionString,
        Guid outboxId,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token
        );
        OutboxSnapshot? last = null;
        try
        {
            while (true)
            {
                last = await ReadOutboxSnapshotAsync(connectionString, outboxId, linkedCancellation.Token);
                if (last is { State: "Completed" or "Rejected" })
                {
                    return last;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), linkedCancellation.Token);
            }
        }
        catch (OperationCanceledException)
            when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return last;
        }
    }

    private static async Task<OutboxSnapshot?> ReadOutboxSnapshotAsync(
        string connectionString,
        Guid outboxId,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT state, attempt, create_by, next_attempt_at FROM {OutboxTableName()} WHERE id = @outbox_id";
        command.Parameters.AddWithValue("outbox_id", outboxId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OutboxSnapshot(reader.GetString(0), reader.GetInt32(1), reader.GetString(2))
            {
                NextAttemptAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            }
            : null;
    }

    private static string OutboxTableName() =>
        new NpgsqlCommandBuilder().QuoteIdentifier(
            $"{ExtensionDbContext.GetTablePrefix(ExtensionDatabaseTestHost.ExtensionId)}__outbox"
        );

    private static string ReadPostgresConnectionString()
    {
        var connectionString = IsolatedTestConnections.Postgres;
        if (connectionString is null)
        {
            Assert.Skip("Set ConnectionString in .scratch/postgres-tests.json to an isolated PostgreSQL server.");
        }
        return connectionString!;
    }

    private sealed record PostgresLockBackend(int ProcessId, DateTime BackendStartedAt);

    private sealed record OutboxSnapshot(string State, int Attempt, string CreateBy)
    {
        public DateTimeOffset? NextAttemptAt { get; init; }
    }

    private sealed class PostgresFixture(
        ExtensionDatabaseMigrationTests.MigrationTestDatabase database,
        ExtensionDatabaseTestHost host,
        string extensionConnectionString,
        string lockConnectionString,
        string lockApplicationName
    ) : IAsyncDisposable
    {
        public ExtensionDatabaseTestHost Host => host;

        public string ExtensionConnectionString => extensionConnectionString;

        public string LockConnectionString => lockConnectionString;

        public string LockApplicationName => lockApplicationName;

        public async ValueTask DisposeAsync()
        {
            await host.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
