using System.Data.Common;
using System.Security.Claims;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Durable;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Tests;

public sealed class DurableExecutionScopeMaintenanceTests : IDisposable
{
    private readonly IDisposable _user = UserInfoUtil.Push(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))
    );

    public void Dispose() => _user.Dispose();

    [Fact]
    public async Task BackfillAsync_UnexpectedLockCancellation_PropagatesWithoutQuarantine()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest: "broken");
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(token);
        var maintenance = new DurableExecutionScopeMaintenance(
            database.Context,
            new UnexpectedCancellationLock(),
            TimeProvider.System,
            database.Logger
        );

        // Act
        await Assert.ThrowsAsync<OperationCanceledException>(() => maintenance.BackfillAsync(token));

        // Assert
        var persisted = await database
            .Context.DurableExecutions.Select(item => new { item.Status, item.ScopeBackfilled })
            .SingleAsync(token);
        Assert.False(persisted.ScopeBackfilled);
        Assert.Equal(DurableExecutionStatus.Queued, persisted.Status);
        Assert.Empty(database.Logger.Messages);
    }

    [Fact]
    public async Task BackfillAsync_CallerCancelsLockWait_PropagatesCancellation()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        database.Context.Add(CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest: "broken"));
        await database.Context.SaveChangesAsync(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var applicationLock = new BusyLock(() => cancellation.Cancel());
        var maintenance = new DurableExecutionScopeMaintenance(
            database.Context,
            applicationLock,
            TimeProvider.System,
            database.Logger
        );

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => maintenance.BackfillAsync(cancellation.Token));

        // Assert
        Assert.False(await database.Context.DurableExecutions.Select(row => row.ScopeBackfilled).SingleAsync(token));
        Assert.Empty(database.Logger.Messages);
    }

    [Fact]
    public async Task StartupRecovery_UsesSystemScope_AndRestoresTheCaller()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), "foreign");
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(token);
        var services = new ServiceCollection();
        services.AddSingleton<IDurableExecutionScopeMaintenance>(database.Maintenance);
        await using var provider = services.BuildServiceProvider();

        // Act
        await DbSeeder.RecoverDurableExecutionScopesAsync(provider, token);

        // Assert
        Assert.Equal("owner", UserInfoUtil.RequiredUserId);
        using var system = UserInfoUtil.PushSystemScope();
        Assert.True(await database.Context.DurableExecutions.Select(item => item.ScopeBackfilled).SingleAsync(token));
    }

    [Fact]
    public async Task ActiveQuery_MatchesTerminalRule_AndTranslatesForBothProviders()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        foreach (var status in Enum.GetValues<DurableExecutionStatus>())
        {
            var row = CreateExecution(projectId, conversationId);
            row.Status = status;
            row.ScopeBackfilled = true;
            database.Context.Add(row);
        }
        await database.Context.SaveChangesAsync(token);

        // Act
        var statuses = await database
            .Context.DurableExecutions.InConversation(projectId, conversationId, "owner")
            .Where(DurableExecutionQueries.Active)
            .OrderBy(row => row.Status)
            .Select(row => row.Status)
            .ToArrayAsync(token);
        await using var postgres = new AgwDbContext(
            new DbContextOptionsBuilder<AgwDbContext>()
                .UseNpgsql("Host=localhost;Database=translation_only")
                .UseSnakeCaseNamingConvention()
                .Options
        );
        var sql = postgres
            .DurableExecutions.InConversation(projectId, conversationId, "owner")
            .Where(DurableExecutionQueries.Active)
            .ToQueryString();
        var cursorId = Guid.CreateVersion7();
        var cursorSql = postgres
            .DurableExecutions.Where(row =>
                !row.ScopeBackfilled
                && (string.Compare(row.UserId, "owner") > 0 || row.UserId == "owner" && row.Id.CompareTo(cursorId) > 0)
            )
            .ToQueryString();

        // Assert
        Assert.Equal(
            Enum.GetValues<DurableExecutionStatus>().Where(status => !DurableExecutionQueries.IsTerminal(status)),
            statuses
        );
        Assert.Contains("project_conversation_id", sql, StringComparison.Ordinal);
        Assert.Contains("status", sql, StringComparison.Ordinal);
        Assert.Contains("scope_backfilled", cursorSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackfillAsync_BusyPrefixBeyondBudget_CursorReachesOtherOwners()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var system = UserInfoUtil.PushSystemScope();
        await using var database = await Database.CreateAsync();
        var busy = Enumerable
            .Range(0, 8)
            .Select(_ => CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), "aaa", "broken"))
            .ToArray();
        var healthy = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), "zzz");
        database.Context.AddRange(busy);
        database.Context.Add(healthy);
        await database.Context.SaveChangesAsync(token);
        var maintenance = new DurableExecutionScopeMaintenance(
            database.Context,
            new BusyLock(),
            TimeProvider.System,
            database.Logger
        );

        // Act
        var first = await maintenance.BackfillAsync(token);
        var last = first;
        for (var attempt = 0; attempt < 12 && last.NextCursor != null; attempt++)
        {
            last = await maintenance.BackfillAsync(token, last.NextCursor);
        }

        // Assert
        Assert.NotNull(first.NextCursor);
        Assert.True(first.HasPending);
        Assert.Null(last.NextCursor);
        Assert.True(last.HasPending);
        Assert.True(
            await database
                .Context.DurableExecutions.Where(row => row.Id == healthy.Id)
                .Select(row => row.ScopeBackfilled)
                .SingleAsync(token)
        );
        Assert.Equal(8, await database.Context.DurableExecutions.CountAsync(row => !row.ScopeBackfilled, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryBeginSegmentAsync_CorruptManifest_QuarantinesWithoutStarting(bool corruptCiphertext)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            manifest: corruptCiphertext ? null : "broken-json"
        );
        row.ScopeBackfilled = true;
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(token);
        if (corruptCiphertext)
        {
            await database.Context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE durable_execution SET manifest_json = 'agwenc:v1:broken' WHERE id = {row.Id}",
                token
            );
        }
        var store = new DurableExecutionStore(
            database.Context,
            TimeProvider.System,
            database.Locks,
            database.Maintenance
        );
        await using var lease = await database.Locks.AcquireAsync(DurableExecutionLock.GetResourceName(row.Id), token);

        // Act
        var snapshot = await store.TryBeginSegmentAsync(row.Id, TimeProvider.System.GetUtcNow(), token);

        // Assert
        Assert.Null(snapshot);
        var persisted = await database
            .Context.DurableExecutions.Select(item => new
            {
                item.Status,
                item.ProjectId,
                item.ProjectConversationId,
            })
            .SingleAsync(token);
        Assert.Equal(DurableExecutionStatus.Failed, persisted.Status);
        Assert.Equal(row.ProjectId, persisted.ProjectId);
        Assert.Equal(row.ProjectConversationId, persisted.ProjectConversationId);
        Assert.Single(database.Logger.Messages);
    }

    [Fact]
    public async Task TryBeginSegmentAsync_HealthyRecord_ReadsExecutionOnce()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7());
        row.ScopeBackfilled = true;
        database.Context.Projects.Add(
            new Agw.Shared.Data.Entities.Projects.Project { Id = row.ProjectId!.Value, CreateBy = row.UserId }
        );
        database.Context.ProjectConversations.Add(
            new Agw.Shared.Data.Entities.Projects.ProjectConversation
            {
                Id = row.ProjectConversationId!.Value,
                ProjectId = row.ProjectId.Value,
                ContextId = "context-1",
                CreateBy = row.UserId,
            }
        );
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(token);
        var store = new DurableExecutionStore(
            database.Context,
            TimeProvider.System,
            database.Locks,
            database.Maintenance
        );
        await using var lease = await database.Locks.AcquireAsync(DurableExecutionLock.GetResourceName(row.Id), token);
        database.Commands.Clear();

        // Act
        var snapshot = await store.TryBeginSegmentAsync(row.Id, TimeProvider.System.GetUtcNow(), token);

        // Assert
        Assert.NotNull(snapshot);
        Assert.Equal(DurableExecutionStatus.Running, snapshot.Status);
        Assert.Single(
            database.Commands,
            sql =>
                sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("\"durable_execution\"", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task GetRunnableExecutionIdsAsync_UnresolvedRow_DoesNotHideIndexedWork()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var pending = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest: "broken");
        var healthy = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7());
        healthy.ScopeBackfilled = true;
        database.Context.AddRange(pending, healthy);
        await database.Context.SaveChangesAsync(token);
        var store = new DurableExecutionStore(
            database.Context,
            TimeProvider.System,
            database.Locks,
            database.Maintenance
        );

        // Act
        var ids = await store.GetRunnableExecutionIdsAsync(TimeProvider.System.GetUtcNow(), 1, token);

        // Assert
        Assert.Equal(healthy.Id, Assert.Single(ids));
    }

    [Fact]
    public async Task BackfillAsync_BusyCorruptRow_DoesNotStarveHealthyRows()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var busy = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest: "broken");
        var healthy = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7());
        database.Context.AddRange(busy, healthy);
        await database.Context.SaveChangesAsync(token);
        await using var lease = await database.Locks.AcquireAsync(DurableExecutionLock.GetResourceName(busy.Id), token);

        // Act
        await database.Maintenance.BackfillAsync(token);

        // Assert
        Assert.True(
            await database
                .Context.DurableExecutions.Where(row => row.Id == healthy.Id)
                .Select(row => row.ScopeBackfilled)
                .SingleAsync(token)
        );
        Assert.False(
            await database
                .Context.DurableExecutions.Where(row => row.Id == busy.Id)
                .Select(row => row.ScopeBackfilled)
                .SingleAsync(token)
        );
        Assert.Empty(database.Logger.Messages);
    }

    [Fact]
    public async Task BackfillAsync_PeerAlreadyStamped_DoesNotFailTheBatch()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7());
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(token);
        await database.Context.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER peer_stamp BEFORE UPDATE OF scope_backfilled ON durable_execution
            WHEN OLD.scope_backfilled = 0
            BEGIN
                UPDATE durable_execution SET scope_backfilled = 1 WHERE id = OLD.id;
                SELECT RAISE(IGNORE);
            END;
            """,
            token
        );

        // Act
        await database.Maintenance.BackfillAsync(token);

        // Assert
        Assert.True(await database.Context.DurableExecutions.Select(item => item.ScopeBackfilled).SingleAsync(token));
        Assert.Empty(database.Logger.Messages);
    }

    [Fact]
    public async Task ValidateLockedExecutionAsync_InconsistentIndex_InvalidatesWrongScope()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7());
        row.ProjectId = Guid.CreateVersion7();
        row.ScopeBackfilled = true;
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(token);
        await using var lease = await database.Locks.AcquireAsync(DurableExecutionLock.GetResourceName(row.Id), token);

        // Act
        var valid = await database.Maintenance.ValidateLockedExecutionAsync(row.Id, token);

        // Assert
        Assert.False(valid);
        var persisted = await database.Context.DurableExecutions.AsNoTracking().SingleAsync(token);
        Assert.Null(persisted.ProjectId);
        Assert.Null(persisted.ProjectConversationId);
        Assert.Equal(DurableExecutionStatus.Failed, persisted.Status);
        Assert.Equal(row.ManifestJson, persisted.ManifestJson);
    }

    [Fact]
    public async Task BackfillAsync_DerivedMetadata_PreservesBusinessAuditAndVersion()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7());
        row.UpdateBy = "original-actor";
        row.UpdateTime = TimeProvider.System.GetUtcNow().AddDays(-7);
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(token);

        // Act
        await database.Maintenance.BackfillAsync(token);

        // Assert
        var persisted = await database.Context.DurableExecutions.AsNoTracking().SingleAsync(token);
        Assert.True(persisted.ScopeBackfilled);
        Assert.Equal(row.UpdateBy, persisted.UpdateBy);
        Assert.Equal(row.UpdateTime, persisted.UpdateTime);
        Assert.Equal(row.StateVersion, persisted.StateVersion);
        Assert.Equal(row.StateChangedAt, persisted.StateChangedAt);
    }

    [Fact]
    public async Task BackfillAsync_MultipleBatches_ProcessesEveryLegacyRow()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var rows = Enumerable.Range(0, 130).Select(_ => CreateExecution(projectId, conversationId)).ToArray();
        foreach (var row in rows)
        {
            row.ProjectId = null;
            row.ProjectConversationId = null;
            row.Status = DurableExecutionStatus.Completed;
        }
        database.Context.AddRange(rows);
        await database.Context.SaveChangesAsync(token);

        // Act
        await database.Maintenance.BackfillAsync(token);

        // Assert
        Assert.Equal(
            130,
            await database.Context.DurableExecutions.CountAsync(
                row =>
                    row.ScopeBackfilled
                    && row.ProjectId == projectId
                    && row.ProjectConversationId == conversationId
                    && row.Status == DurableExecutionStatus.Completed,
                token
            )
        );
        Assert.Empty(database.Logger.Messages);
    }

    [Fact]
    public async Task BackfillAsync_LegacyRows_RecoversScopeAndQuarantinesCorruptionOnce()
    {
        // Arrange
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var valid = CreateExecution(projectId, conversationId);
        var brokenJson = CreateExecution(projectId, conversationId, manifest: "sensitive-broken-json");
        var brokenCiphertext = CreateExecution(projectId, conversationId);
        var foreign = CreateExecution(projectId, conversationId, "foreign");
        foreach (var row in new[] { valid, brokenJson, brokenCiphertext, foreign })
        {
            row.ProjectId = null;
            row.ProjectConversationId = null;
        }
        database.Context.AddRange(valid, brokenJson, brokenCiphertext, foreign);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await database.Context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE durable_execution SET manifest_json = 'agwenc:v1:broken' WHERE id = {brokenCiphertext.Id}",
            TestContext.Current.CancellationToken
        );
        var original = await database
            .Context.DurableExecutions.Where(row => row.Id == valid.Id)
            .Select(row => row.ManifestJson)
            .SingleAsync(TestContext.Current.CancellationToken);

        // Act
        await database.Maintenance.BackfillAsync(TestContext.Current.CancellationToken);
        database.Commands.Clear();
        await database.Maintenance.BackfillAsync(TestContext.Current.CancellationToken);

        // Assert
        var rows = await database
            .Context.DurableExecutions.AsNoTracking()
            .Select(row => new
            {
                row.Id,
                row.ProjectId,
                row.ProjectConversationId,
                row.ScopeBackfilled,
                row.Status,
                row.ManifestJson,
            })
            .ToArrayAsync(TestContext.Current.CancellationToken);
        var recovered = Assert.Single(rows, row => row.Id == valid.Id);
        Assert.Equal(projectId, recovered.ProjectId);
        Assert.Equal(conversationId, recovered.ProjectConversationId);
        Assert.True(recovered.ScopeBackfilled);
        Assert.Equal(DurableExecutionStatus.Queued, recovered.Status);
        Assert.Equal(original, recovered.ManifestJson);
        foreach (var id in new[] { brokenJson.Id, brokenCiphertext.Id })
        {
            var quarantined = Assert.Single(rows, row => row.Id == id);
            Assert.Equal(DurableExecutionStatus.Failed, quarantined.Status);
            Assert.Null(quarantined.ProjectId);
            Assert.True(quarantined.ScopeBackfilled);
        }
        Assert.Equal(2, database.Logger.Messages.Count);
        Assert.All(database.Logger.Messages, message => Assert.DoesNotContain("sensitive-broken-json", message));
        Assert.DoesNotContain("manifest_json", database.Commands[0], StringComparison.OrdinalIgnoreCase);
        using var system = UserInfoUtil.PushSystemScope();
        Assert.False(
            await database
                .Context.DurableExecutions.Where(row => row.Id == foreign.Id)
                .Select(row => row.ScopeBackfilled)
                .SingleAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task RepairAndCheckActiveExecutionsAsync_IndexedForeignProjectCorruption_DoesNotReadItsManifest()
    {
        // Arrange
        await using var database = await Database.CreateAsync();
        var target = Guid.CreateVersion7();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest: "unrelated-bad-manifest");
        row.ScopeBackfilled = true;
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.Commands.Clear();

        // Act
        var active = await database.Maintenance.RepairAndCheckActiveExecutionsAsync(
            target,
            Guid.CreateVersion7(),
            "owner",
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.False(active);
        Assert.All(
            database.Commands,
            sql => Assert.DoesNotContain("manifest_json", sql, StringComparison.OrdinalIgnoreCase)
        );
        Assert.Empty(database.Logger.Messages);
    }

    [Fact]
    public async Task RepairAndCheckActiveExecutionsAsync_CorruptTargetExecution_QuarantinesBeforeAllowingResume()
    {
        // Arrange
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest: "{\"task\":null}");
        row.ScopeBackfilled = true;
        row.Status = DurableExecutionStatus.WaitingForHuman;
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var active = await database.Maintenance.RepairAndCheckActiveExecutionsAsync(
            row.ProjectId!.Value,
            row.ProjectConversationId!.Value,
            "owner",
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.False(active);
        Assert.Equal(
            DurableExecutionStatus.Failed,
            await database
                .Context.DurableExecutions.Where(item => item.Id == row.Id)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken)
        );
        Assert.Single(database.Logger.Messages);
    }

    [Fact]
    public async Task RepairAndCheckActiveExecutionsAsync_WorkerOwnsLock_PreservesRunningStateAndBlocksResume()
    {
        // Arrange
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest: "broken");
        row.ScopeBackfilled = true;
        row.Status = DurableExecutionStatus.Running;
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using var executionLock = await database.Locks.AcquireAsync(
            DurableExecutionLock.GetResourceName(row.Id),
            TestContext.Current.CancellationToken
        );

        // Act
        var active = await database.Maintenance.RepairAndCheckActiveExecutionsAsync(
            row.ProjectId!.Value,
            row.ProjectConversationId!.Value,
            "owner",
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.True(active);
        Assert.Equal(
            DurableExecutionStatus.Running,
            await database
                .Context.DurableExecutions.Where(item => item.Id == row.Id)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken)
        );
        Assert.Empty(database.Logger.Messages);
    }

    [Fact]
    public async Task ValidateLockedExecutionAsync_ConcurrentVersionChange_DoesNotOverwriteWinner()
    {
        // Arrange
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest: "broken");
        row.ScopeBackfilled = true;
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await database.Context.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER skip_quarantine BEFORE UPDATE OF scope_backfilled ON durable_execution
            BEGIN
                UPDATE durable_execution SET status = 4,
                    state_version = '11111111-1111-1111-1111-111111111111' WHERE id = OLD.id;
                SELECT RAISE(IGNORE);
            END;
            """,
            TestContext.Current.CancellationToken
        );
        await using var executionLock = await database.Locks.AcquireAsync(
            DurableExecutionLock.GetResourceName(row.Id),
            TestContext.Current.CancellationToken
        );

        // Act
        var valid = await database.Maintenance.ValidateLockedExecutionAsync(
            row.Id,
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.False(valid);
        Assert.Equal(
            DurableExecutionStatus.Completed,
            await database
                .Context.DurableExecutions.Where(item => item.Id == row.Id)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken)
        );
        Assert.Empty(database.Logger.Messages);
    }

    internal static DurableExecutionRecord CreateExecution(
        Guid projectId,
        Guid conversationId,
        string owner = "owner",
        string? manifest = null
    )
    {
        var id = Guid.CreateVersion7();
        return new DurableExecutionRecord
        {
            Id = id,
            UserId = owner,
            CreateBy = owner,
            ProjectId = projectId,
            ProjectConversationId = conversationId,
            ManifestJson =
                manifest
                ?? DurableExecutionJson.Serialize(
                    new DurableExecutionManifest
                    {
                        ExecutionId = id,
                        UserId = owner,
                        AgentId = Guid.CreateVersion7(),
                        AgentType = AgentRuntimeType.Agent,
                        Input = new AgwUserInput { Contents = [] },
                        Task = new DurableProjectTaskSnapshot
                        {
                            ProjectId = projectId,
                            ProjectConversationId = conversationId,
                            TaskId = Guid.CreateVersion7(),
                            ContextId = "context",
                        },
                        Settings = DurableExecutionSettings.FromSettings(
                            ExecutionSettings.FromCommand(new SettingCommand(projectId, contextId: "context"))
                        ),
                    }
                ),
            StateVersion = Guid.CreateVersion7(),
            StateChangedAt = TimeProvider.System.GetUtcNow(),
        };
    }

    private sealed class Database : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AgwDbContext Context { get; }
        public InMemoryApplicationLock Locks { get; } = new();
        public RecordingLogger Logger { get; } = new();
        public List<string> Commands { get; } = [];
        public DurableExecutionScopeMaintenance Maintenance { get; }

        private Database(SqliteConnection connection)
        {
            _connection = connection;
            Context = new AgwDbContext(
                new DbContextOptionsBuilder<AgwDbContext>()
                    .UseSqlite(connection)
                    .UseSnakeCaseNamingConvention()
                    .AddInterceptors(new CommandRecorder(Commands))
                    .Options
            );
            Maintenance = new DurableExecutionScopeMaintenance(Context, Locks, TimeProvider.System, Logger);
        }

        public static async Task<Database> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var database = new Database(connection);
            await database.Context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return database;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class BusyLock : IApplicationLock
    {
        private readonly Action? _onAcquire;

        public BusyLock(Action? onAcquire = null)
        {
            _onAcquire = onAcquire;
        }

        public async Task<IApplicationLockLease> AcquireAsync(string resourceName, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<IApplicationLockLease>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            _onAcquire?.Invoke();
            return await completion.Task.ConfigureAwait(false);
        }
    }

    private sealed class UnexpectedCancellationLock : IApplicationLock
    {
        public Task<IApplicationLockLease> AcquireAsync(string resourceName, CancellationToken cancellationToken) =>
            Task.FromException<IApplicationLockLease>(
                new OperationCanceledException("Provider cancelled for an unrelated reason.")
            );
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly List<string> _commands;

        public CommandRecorder(List<string> commands)
        {
            _commands = commands;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            _commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingLogger : ILogger<DurableExecutionScopeMaintenance>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));
    }
}
