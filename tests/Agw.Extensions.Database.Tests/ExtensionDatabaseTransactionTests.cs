using System.Data;
using System.Data.Common;
using System.Text.Json;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Database;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Extensions.Database.Tests;

public sealed class ExtensionDatabaseTransactionTests
{
    private static readonly Guid ExtensionId = ExtensionDatabaseTestHost.ExtensionId;
    private static readonly string ProbeTableName = $"zext_{ExtensionId:N}_database_probe";
    private static readonly string InboxTableName = $"zext_{ExtensionId:N}___inbox";
    private static readonly string OutboxTableName = $"zext_{ExtensionId:N}___outbox";

    [Fact]
    public async Task EventHandler_RetryRollsBackInboxBusinessDataAndOutbox()
    {
        await using var fixture = await CreateFixtureAsync(hostRole: "Standalone");
        Guid deliveryId = Guid.CreateVersion7();
        await using IExtensionGenerationLease lease = await fixture.Host.Runtime.AcquireCurrentAsync(
            ExtensionId,
            TestContext.Current.CancellationToken
        );

        ExtensionEventHandlerResult result = await fixture.Host.Runtime.InvokeEventHandlerAsync(
            lease,
            new ExtensionEventHandlerInvocation
            {
                HandlerId = "database",
                UserId = "alice",
                Delivery = CreateDelivery(deliveryId, id: 101, value: "retry", retry: true),
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ExtensionEventHandlerResultKind.Retry, result.Kind);
        Assert.Equal(0, await CountAsync(fixture.Database, ProbeTableName));
        Assert.Equal(0, await CountAsync(fixture.Database, InboxTableName));
        Assert.Equal(0, await CountAsync(fixture.Database, OutboxTableName));
    }

    [Fact]
    public async Task EventHandler_SuccessCommitsInboxBusinessDataAndOutboxAndDeduplicatesDelivery()
    {
        await using var fixture = await CreateFixtureAsync(hostRole: "Standalone");
        Guid deliveryId = Guid.CreateVersion7();
        await using IExtensionGenerationLease lease = await fixture.Host.Runtime.AcquireCurrentAsync(
            ExtensionId,
            TestContext.Current.CancellationToken
        );
        var invocation = new ExtensionEventHandlerInvocation
        {
            HandlerId = "database",
            UserId = "alice",
            Delivery = CreateDelivery(deliveryId, id: 102, value: "stored", retry: false),
        };

        ExtensionEventHandlerResult first = await fixture.Host.Runtime.InvokeEventHandlerAsync(
            lease,
            invocation,
            TestContext.Current.CancellationToken
        );
        ExtensionEventHandlerResult duplicate = await fixture.Host.Runtime.InvokeEventHandlerAsync(
            lease,
            invocation,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, first.Kind);
        Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, duplicate.Kind);
        Assert.Equal(1, await CountAsync(fixture.Database, ProbeTableName));
        Assert.Equal(1, await CountAsync(fixture.Database, InboxTableName));
        Assert.Equal(1, await CountAsync(fixture.Database, OutboxTableName));
        Assert.Equal(
            "alice",
            await ReadStringAsync(fixture.Database, OutboxTableName, "create_by", "idempotency_key", "probe:102")
        );
    }

    [Fact]
    public async Task GetDatabaseValues_IgnoreQueryFilters_IsRejected()
    {
        await using var fixture = await CreateFixtureAsync();
        await InvokeAsync(fixture, "bob", "create", id: 1, value: "private");
        await InvokeAsync(fixture, "alice", "create", id: 2, value: "own");
        await InvokeAsync(fixture, "alice", "update", id: 2, value: "updated");

        AgwException exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Host.InvokeEndpointAsync(
                "alice",
                "database",
                new DatabaseOperationRequest("get-database-values", OwnId: 2, ForeignId: 1),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, exception.Code);
    }

    [Fact]
    public async Task Query_CapturedScalarAndCollectionChanges_ReturnsCurrentValues()
    {
        await using var fixture = await CreateFixtureAsync();
        await InvokeAsync(fixture, "alice", "create", id: 1, value: "first");
        await InvokeAsync(fixture, "alice", "create", id: 2, value: "second");

        JsonElement? response = await fixture.Host.InvokeEndpointAsync(
            "alice",
            "database",
            new DatabaseOperationRequest(
                "read-captured-query",
                Id: 1,
                Value: "first",
                SecondId: 2,
                SecondValue: "second",
                Values: ["first"],
                SecondValues: ["second"]
            ),
            TestContext.Current.CancellationToken
        );

        Assert.True(response.HasValue);
        Assert.Equal(2, response.Value.GetArrayLength());
        Assert.Equal("first", response.Value[0].GetProperty("value").GetString());
        Assert.Equal("second", response.Value[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Query_OpenReader_DoesNotReserveSqliteWriter()
    {
        await using var fixture = await CreateFixtureAsync(sqliteFile: true);
        await InvokeAsync(fixture, "alice", "create", id: 1, value: "before");
        await using ExtensionDatabaseInvocation invocation = await fixture.Host.BeginEndpointInvocationAsync(
            "alice",
            "open-reader",
            null,
            TestContext.Current.CancellationToken
        );
        await using IAsyncEnumerator<ReadOnlyMemory<byte>> reader = invocation.BodyStream!.GetAsyncEnumerator(
            TestContext.Current.CancellationToken
        );

        Assert.True(await reader.MoveNextAsync());
        await using DbConnection connection = await fixture.Database.OpenConnectionAsync(
            TestContext.Current.CancellationToken
        );
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = $"UPDATE \"zext_{ExtensionId:N}_database_probe\" SET value = @value WHERE id = @id";
        AddParameter(command, "@value", "after", DbType.String);
        AddParameter(command, "@id", 1, DbType.Int32);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        using JsonDocument row = JsonDocument.Parse(reader.Current);
        Assert.Equal("before", row.RootElement.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Query_UnsupportedOwnershipAndSqlEntrypoints_FailBeforeExecution(int operationIndex)
    {
        await using var fixture = await CreateFixtureAsync();

        AgwException exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Host.InvokeEndpointAsync(
                "alice",
                "database",
                new DatabaseOperationRequest("unsupported", OperationIndex: operationIndex),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, exception.Code);
    }

    [Fact]
    public async Task SaveChanges_OwnershipFailure_DisposesTransactionAndReleasesSqliteLock()
    {
        await using var fixture = await CreateFixtureAsync();
        await InvokeAsync(fixture, "alice", "create", id: 1, value: "owned");

        AgwException exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Host.InvokeEndpointAsync(
                "bob",
                "database",
                new DatabaseOperationRequest("attach-update", 1, Value: "changed", CreateBy: "bob"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        await AssertMigrationLockAvailableAsync(fixture);
    }

    [Fact]
    public async Task BeginTransaction_DisposeWithoutCommit_ReleasesSqliteLock()
    {
        await using var fixture = await CreateFixtureAsync();

        await InvokeAsync(fixture, "alice", "begin-transaction");

        await AssertMigrationLockAvailableAsync(fixture);
    }

    [Fact]
    public async Task TransactionStarting_BusySqliteBeginReleasesBusinessAccess()
    {
        await using var fixture = await CreateFixtureAsync(sqliteFile: true);
        await using DbConnection writerConnection = await fixture.Database.OpenConnectionAsync(
            TestContext.Current.CancellationToken
        );
        await using DbTransaction writerTransaction = await writerConnection.BeginTransactionAsync(
            TestContext.Current.CancellationToken
        );
        await using (var command = writerConnection.CreateCommand())
        {
            command.Transaction = writerTransaction;
            command.CommandText = "UPDATE setting SET version = version";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        AgwException exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Host.InvokeEndpointAsync(
                "alice",
                "database",
                new DatabaseOperationRequest("begin-transaction"),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(ErrorCodes.ExtensionUnavailable.Code, exception.Code);
        await writerTransaction.RollbackAsync(TestContext.Current.CancellationToken);

        ExtensionDatabaseLockCoordinator coordinator =
            fixture.Host.Services.GetRequiredService<ExtensionDatabaseLockCoordinator>();
        await using DbConnection migrationConnection = await fixture.Database.OpenConnectionAsync(
            TestContext.Current.CancellationToken
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using IAsyncDisposable migrationAccess = await coordinator.AcquireMigrationAccessAsync(
            ExtensionId,
            ExtensionDatabaseProvider.Sqlite,
            migrationConnection,
            timeout.Token
        );
    }

    [Fact]
    public async Task BeginTransaction_MigrationLockAcquiredFirst_DoesNotReserveSqliteWriter()
    {
        await using var fixture = await CreateFixtureAsync(sqliteFile: true);
        ExtensionDatabaseLockCoordinator coordinator =
            fixture.Host.Services.GetRequiredService<ExtensionDatabaseLockCoordinator>();
        await using DbConnection migrationConnection = await fixture.Database.OpenConnectionAsync(
            TestContext.Current.CancellationToken
        );
        await using (var timeout = migrationConnection.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout = 500";
            await timeout.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        IAsyncDisposable migrationAccess = await coordinator.AcquireMigrationAccessAsync(
            ExtensionId,
            ExtensionDatabaseProvider.Sqlite,
            migrationConnection,
            TestContext.Current.CancellationToken
        );
        try
        {
            Task<JsonElement?> businessWrite = fixture.Host.InvokeEndpointAsync(
                "alice",
                "database",
                new DatabaseOperationRequest("write-transaction", 3, Value: "queued"),
                TestContext.Current.CancellationToken
            );
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            Assert.False(businessWrite.IsCompleted);

            await using DbTransaction migrationTransaction = await migrationConnection
                .BeginTransactionAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await migrationTransaction.CommitAsync(TestContext.Current.CancellationToken);
            await migrationAccess.DisposeAsync();

            JsonElement? result = await businessWrite.WaitAsync(
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken
            );
            Assert.Equal("queued", result?.GetProperty("value").GetString());
        }
        finally
        {
            await migrationAccess.DisposeAsync();
        }
    }

    [Fact]
    public async Task CommitAsync_DisabledDuringDrain_AllowsAcceptedWorkToFinish()
    {
        await using var fixture = await CreateFixtureAsync();
        await using ExtensionDatabaseInvocation invocation = await fixture.Host.BeginEndpointInvocationAsync(
            "alice",
            "hold-write",
            new DatabaseOperationRequest("hold", 4, Value: "accepted before disable"),
            TestContext.Current.CancellationToken
        );
        await using IAsyncEnumerator<ReadOnlyMemory<byte>> body = invocation.BodyStream!.GetAsyncEnumerator(
            TestContext.Current.CancellationToken
        );

        Assert.True(await body.MoveNextAsync());
        Assert.False(
            await fixture.Host.Runtime.DrainAsync(
                fixture.Generation.GenerationId,
                TimeSpan.FromMilliseconds(100),
                TestContext.Current.CancellationToken
            )
        );
        Task<ExtensionEnablementState> disableRequest = Task.Run(
            () => fixture.Host.SetEnabledAsync(false, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.False(disableRequest.IsCompleted);

        Assert.True(await body.MoveNextAsync());
        Assert.False(await body.MoveNextAsync());
        await invocation.CompleteAsync(TestContext.Current.CancellationToken);
        ExtensionEnablementState disabled = await disableRequest.WaitAsync(
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken
        );
        Assert.False(disabled.Enabled);

        await using DbConnection connection = await fixture.Database.OpenConnectionAsync(
            TestContext.Current.CancellationToken
        );
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"zext_{ExtensionId:N}_database_probe\" WHERE id = @id";
        AddParameter(command, "@id", 4, DbType.Int32);
        Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcquireCurrent_DisabledWithoutLiveGenerationLease_IsRejected()
    {
        await using var fixture = await CreateFixtureAsync();
        await fixture.Host.SetEnabledAsync(false, TestContext.Current.CancellationToken);

        AgwException exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Host.Runtime.AcquireCurrentAsync(ExtensionId, TestContext.Current.CancellationToken).AsTask()
        );

        Assert.Equal(ErrorCodes.ExtensionUnavailable.Code, exception.Code);
    }

    private static async Task<DatabaseFixture> CreateFixtureAsync(bool sqliteFile = true, string hostRole = "DataPlane")
    {
        ExtensionDatabaseMigrationTests.MigrationTestDatabase database =
            await ExtensionDatabaseMigrationTests.MigrationTestDatabase.CreateAsync(
                ExtensionDatabaseProvider.Sqlite,
                null,
                sqliteFile
            );
        ExtensionDatabaseTestHost host = await ExtensionDatabaseTestHost.CreateAsync(database, hostRole: hostRole);
        try
        {
            ExtensionGenerationSnapshot generation = await host.LoadAndActivateAsync(
                TestContext.Current.CancellationToken
            );
            return new DatabaseFixture(database, host, generation);
        }
        catch
        {
            await host.DisposeAsync();
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<JsonElement> InvokeAsync(
        DatabaseFixture fixture,
        string? userId,
        string operation,
        int? id = null,
        int? valueId = null,
        string? value = null
    )
    {
        JsonElement? response = await fixture.Host.InvokeEndpointAsync(
            userId,
            "database",
            new DatabaseOperationRequest(operation, id, Value: value, OperationIndex: valueId),
            TestContext.Current.CancellationToken
        );
        Assert.True(response.HasValue);
        return response!.Value;
    }

    private static async Task AssertMigrationLockAvailableAsync(DatabaseFixture fixture)
    {
        await using DbConnection connection = await fixture.Database.OpenConnectionAsync(
            TestContext.Current.CancellationToken
        );
        await using IAsyncDisposable migrationAccess = await fixture
            .Host.Services.GetRequiredService<ExtensionDatabaseLockCoordinator>()
            .AcquireMigrationAccessAsync(
                ExtensionId,
                ExtensionDatabaseProvider.Sqlite,
                connection,
                TestContext.Current.CancellationToken
            )
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value, DbType type)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static ExtensionEventDelivery CreateDelivery(Guid deliveryId, int id, string value, bool retry) =>
        new()
        {
            Event = new CloudEventEnvelope
            {
                Id = deliveryId.ToString("D"),
                Source = "/test/database",
                Type = "database.probe.requested",
                Data = new CloudEventData
                {
                    Kind = CloudEventDataKind.Json,
                    Json = JsonSerializer.SerializeToElement(
                        new
                        {
                            id,
                            value,
                            retry,
                        }
                    ),
                },
            },
            DeliveryId = deliveryId,
            Attempt = 1,
            SubscriptionId = Guid.CreateVersion7(),
            EventId = Guid.CreateVersion7(),
            SourceId = Guid.CreateVersion7(),
            ReceivedAt = DateTimeOffset.UnixEpoch,
            ChainId = Guid.CreateVersion7(),
            Hops = 0,
        };

    private static async Task<long> CountAsync(
        ExtensionDatabaseMigrationTests.MigrationTestDatabase database,
        string tableName
    )
    {
        await using DbConnection connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\"";
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<string?> ReadStringAsync(
        ExtensionDatabaseMigrationTests.MigrationTestDatabase database,
        string tableName,
        string selectColumn,
        string whereColumn,
        string value
    )
    {
        await using DbConnection connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT \"{selectColumn}\" FROM \"{tableName}\" WHERE \"{whereColumn}\" = @value";
        AddParameter(command, "@value", value, DbType.String);
        return (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private sealed record DatabaseOperationRequest(
        string Operation,
        int? Id = null,
        int? OwnId = null,
        int? ForeignId = null,
        int? RootId = null,
        int? ChildId = null,
        int? OperationIndex = null,
        string? Value = null,
        string? CreateBy = null,
        int? SecondId = null,
        string? SecondValue = null,
        string[]? Values = null,
        string[]? SecondValues = null
    );

    private sealed class DatabaseFixture(
        ExtensionDatabaseMigrationTests.MigrationTestDatabase database,
        ExtensionDatabaseTestHost host,
        ExtensionGenerationSnapshot generation
    ) : IAsyncDisposable
    {
        public ExtensionDatabaseMigrationTests.MigrationTestDatabase Database { get; } = database;

        public ExtensionDatabaseTestHost Host { get; } = host;

        public ExtensionGenerationSnapshot Generation { get; } = generation;

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
