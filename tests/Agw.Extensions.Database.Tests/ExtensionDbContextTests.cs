using System.Text.Json;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Xunit;

namespace Agw.Extensions.Database.Tests;

public sealed class ExtensionDbContextTests
{
    [Fact]
    public async Task Model_UsesExtensionNamespaceAndSnakeCaseIdentifiers()
    {
        await using var fixture = await CreateDatabaseAsync();

        JsonElement model = await InvokeAsync(fixture, "alice", "model");

        Assert.Equal(
            $"zext_{ExtensionDatabaseTestHost.ExtensionId:N}_database_probe",
            model.GetProperty("tableName").GetString()
        );
        string[] columns = model
            .GetProperty("columnNames")
            .EnumerateArray()
            .Select(column => column.GetString()!)
            .ToArray();
        Assert.Contains("id", columns);
        Assert.Contains("create_by", columns);
        Assert.Equal(0, model.GetProperty("foreignKeyCount").GetInt32());
        Assert.Equal(
            "20261107000100_Initial",
            Assert.Single(model.GetProperty("migrationIds").EnumerateArray()).GetString()
        );
    }

    [Fact]
    public async Task Query_EmptyDatabaseReturnsNoRows()
    {
        await using var fixture = await CreateDatabaseAsync();

        JsonElement count = await InvokeAsync(fixture, "alice", "count");

        Assert.Equal(0, count.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Query_ForeignOrMissingOwner_ReturnsNoRows()
    {
        await using var fixture = await CreateDatabaseAsync();
        await InvokeAsync(fixture, "alice", "create", id: 1, value: "owner row");

        Assert.Equal(0, await InvokeCountAsync(fixture, "bob"));
        Assert.Equal(0, await InvokeCountAsync(fixture, null));
    }

    [Fact]
    public async Task SaveChanges_StampsOwnerAndAuditValues()
    {
        await using var fixture = await CreateDatabaseAsync();

        JsonElement created = await InvokeAsync(fixture, "alice", "create", id: 1, value: "first", createBy: "bob");

        Assert.Equal("alice", created.GetProperty("createBy").GetString());
        Assert.NotEqual(default, created.GetProperty("createTime").GetDateTimeOffset());
        Assert.Null(created.GetProperty("updateBy").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("updateTime").ValueKind);

        JsonElement updated = await InvokeAsync(fixture, "alice", "update", id: 1, value: "second");

        Assert.Equal("alice", updated.GetProperty("updateBy").GetString());
        Assert.NotEqual(default, updated.GetProperty("updateTime").GetDateTimeOffset());
    }

    [Fact]
    public async Task SaveChanges_WithoutUserContext_RejectsOwnedRows()
    {
        await using var fixture = await CreateDatabaseAsync();

        AgwException exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Host.InvokeEndpointAsync(
                null,
                "database",
                new DatabaseOperationRequest("create", 1, Value: "anonymous"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.AuthenticationRequired.Code, exception.Code);
    }

    [Fact]
    public async Task SaveChanges_CreateByIsImmutable()
    {
        await using var fixture = await CreateDatabaseAsync();
        await InvokeAsync(fixture, "alice", "create", id: 1, value: "owner row");

        AgwException exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Host.InvokeEndpointAsync(
                "alice",
                "database",
                new DatabaseOperationRequest("owner-change", 1, CreateBy: "bob"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Theory]
    [InlineData("attach-update")]
    [InlineData("attach-delete")]
    public async Task SaveChanges_AttachedForeignOwnerUpdateOrDelete_IsRejected(string operation)
    {
        await using var fixture = await CreateDatabaseAsync();
        await InvokeAsync(fixture, "alice", "create", id: 1, value: "owner row");

        AgwException exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Host.InvokeEndpointAsync(
                "bob",
                "database",
                new DatabaseOperationRequest(operation, 1, Value: "changed", CreateBy: "bob"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Equal(
            "owner row",
            (await InvokeAsync(fixture, "alice", "read", id: 1)).GetProperty("value").GetString()
        );
    }

    [Fact]
    public async Task SaveChanges_ChildWithForeignRoot_IsRejected()
    {
        await using var fixture = await CreateDatabaseAsync();
        await InvokeAsync(fixture, "alice", "create", id: 1, value: "owner root");

        AgwException exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Host.InvokeEndpointAsync(
                "bob",
                "database",
                new DatabaseOperationRequest("add-child", RootId: 1, ChildId: 2, Value: "foreign child"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task SaveChanges_ChildWithForgedUnchangedRoot_IsRejected()
    {
        await using var fixture = await CreateDatabaseAsync();
        await InvokeAsync(fixture, "alice", "create", id: 1, value: "owner root");

        AgwException exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Host.InvokeEndpointAsync(
                "bob",
                "database",
                new DatabaseOperationRequest(
                    "add-forged-child",
                    RootId: 1,
                    ChildId: 2,
                    Value: "forged",
                    CreateBy: "bob"
                ),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task SaveChanges_ChildWithAddedRootInSameUnitOfWork_Succeeds()
    {
        await using var fixture = await CreateDatabaseAsync();

        JsonElement result = await InvokeAsync(fixture, "alice", "add-root-child", id: 1, childId: 2);

        Assert.Equal("alice", result.GetProperty("rootOwner").GetString());
        Assert.Equal("alice", result.GetProperty("childOwner").GetString());
    }

    [Fact]
    public async Task SaveChanges_DeletingRootAndChildrenInSameUnitOfWork_Succeeds()
    {
        await using var fixture = await CreateDatabaseAsync();
        await InvokeAsync(fixture, "alice", "add-root-child", id: 1, childId: 2);

        JsonElement deleted = await InvokeAsync(fixture, "alice", "delete-root-child", id: 1);

        Assert.Equal(1, deleted.GetProperty("count").GetInt32());
        Assert.Equal(0, await InvokeCountAsync(fixture, "alice"));
    }

    [Fact]
    public async Task SaveChanges_AuditValuesUseCurrentActorAndTime()
    {
        await using var fixture = await CreateDatabaseAsync();
        DateTimeOffset beforeSave = TimeProvider.System.GetUtcNow();

        JsonElement created = await InvokeAsync(
            fixture,
            "alice",
            "create",
            id: 1,
            value: "audit row",
            createBy: "bob",
            updateBy: "bob"
        );

        DateTimeOffset afterSave = TimeProvider.System.GetUtcNow();
        Assert.Equal("alice", created.GetProperty("createBy").GetString());
        Assert.InRange(created.GetProperty("createTime").GetDateTimeOffset(), beforeSave, afterSave);
        Assert.Null(created.GetProperty("updateBy").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("updateTime").ValueKind);

        beforeSave = TimeProvider.System.GetUtcNow();
        JsonElement updated = await InvokeAsync(fixture, "alice", "update", id: 1, value: "updated row");
        afterSave = TimeProvider.System.GetUtcNow();

        Assert.Equal("alice", updated.GetProperty("updateBy").GetString());
        Assert.InRange(updated.GetProperty("updateTime").GetDateTimeOffset(), beforeSave, afterSave);
    }

    private static async Task<DatabaseFixture> CreateDatabaseAsync()
    {
        ExtensionDatabaseMigrationTests.MigrationTestDatabase database =
            await ExtensionDatabaseMigrationTests.MigrationTestDatabase.CreateAsync(
                ExtensionDatabaseProvider.Sqlite,
                null,
                sqliteFile: true
            );
        ExtensionDatabaseTestHost host = await ExtensionDatabaseTestHost.CreateAsync(database);
        try
        {
            await host.LoadAndActivateAsync(TestContext.Current.CancellationToken);
            return new DatabaseFixture(database, host);
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
        int? rootId = null,
        int? childId = null,
        string? value = null,
        string? createBy = null,
        string? updateBy = null
    )
    {
        JsonElement? response = await fixture.Host.InvokeEndpointAsync(
            userId,
            "database",
            new DatabaseOperationRequest(operation, id, rootId, childId, value, createBy, updateBy),
            TestContext.Current.CancellationToken
        );
        Assert.True(response.HasValue);
        return response!.Value;
    }

    private static async Task<int> InvokeCountAsync(DatabaseFixture fixture, string? userId)
    {
        JsonElement response = await InvokeAsync(fixture, userId, "count");
        return response.GetProperty("count").GetInt32();
    }

    private sealed record DatabaseOperationRequest(
        string Operation,
        int? Id = null,
        int? RootId = null,
        int? ChildId = null,
        string? Value = null,
        string? CreateBy = null,
        string? UpdateBy = null
    );

    private sealed class DatabaseFixture(
        ExtensionDatabaseMigrationTests.MigrationTestDatabase database,
        ExtensionDatabaseTestHost host
    ) : IAsyncDisposable
    {
        public ExtensionDatabaseTestHost Host { get; } = host;

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
