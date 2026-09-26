using System.Data.Common;
using System.Security.Claims;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Tests;

public sealed class DurableExecutionScopeMaintenanceTests : IDisposable
{
    private readonly IDisposable _user = UserInfoUtil.Push(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))
    );

    public void Dispose() => _user.Dispose();

    [Fact]
    public async Task ValidateExecutionAsync_UnindexedRecord_RejectsWithoutRecoveringScope()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7());
        row.ProjectId = null;
        row.ProjectConversationId = null;
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(token);
        var originalManifest = row.ManifestJson;

        Assert.False(await database.Maintenance.ValidateExecutionAsync(row.Id, token));

        database.Context.ChangeTracker.Clear();
        var unchanged = await database.Context.DurableExecutions.SingleAsync(token);
        Assert.False(unchanged.ScopeBackfilled);
        Assert.Null(unchanged.ProjectId);
        Assert.Null(unchanged.ProjectConversationId);
        Assert.Equal(originalManifest, unchanged.ManifestJson);
        Assert.Equal(DurableExecutionStatus.Failed, unchanged.Status);
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
        // Assert
        Assert.Equal(
            Enum.GetValues<DurableExecutionStatus>().Where(status => !DurableExecutionQueries.IsTerminal(status)),
            statuses
        );
        Assert.Contains("project_conversation_id", sql, StringComparison.Ordinal);
        Assert.Contains("status", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadClaimedAsync_CorruptManifest_QuarantinesWithoutStarting(bool corruptCiphertext)
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

        // Act
        var snapshot = await store.LoadClaimedAsync(row.Id, token);

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
    public async Task LoadClaimedAsync_HealthyRecord_ReadsExecutionOnce()
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
        database.Commands.Clear();

        // Act
        var snapshot = await store.LoadClaimedAsync(row.Id, token);

        // Assert
        Assert.NotNull(snapshot);
        Assert.Equal(row.Status, snapshot.Status);
        Assert.Single(
            database.Commands,
            sql =>
                sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("\"durable_execution\"", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task GetClaimableAsync_UnresolvedRow_DoesNotHideIndexedWork()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var kit = await TurnPersistenceTestKit.CreateAsync();
        var pending = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest: "broken");
        var healthy = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7());
        healthy.ScopeBackfilled = true;
        await using (var context = kit.CreateContext())
        {
            context.AddRange(pending, healthy);
            await context.SaveChangesAsync(token);
        }

        // Act
        IReadOnlyList<Guid> ids;
        using (UserInfoUtil.PushSystemScope())
            ids = await kit.Leases.GetClaimableAsync(1, token);

        // Assert
        Assert.Equal(healthy.Id, Assert.Single(ids));
    }

    [Fact]
    public async Task ValidateExecutionAsync_InconsistentIndex_InvalidatesWrongScope()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7());
        row.ProjectId = Guid.CreateVersion7();
        row.ScopeBackfilled = true;
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(token);

        // Act
        var valid = await database.Maintenance.ValidateExecutionAsync(row.Id, token);

        // Assert
        Assert.False(valid);
        var persisted = await database.Context.DurableExecutions.AsNoTracking().SingleAsync(token);
        Assert.Null(persisted.ProjectId);
        Assert.Null(persisted.ProjectConversationId);
        Assert.Equal(DurableExecutionStatus.Failed, persisted.Status);
        Assert.Equal(row.ManifestJson, persisted.ManifestJson);
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
    public async Task RepairAndCheckActiveExecutionsAsync_WorkerHoldsLease_PreservesRunningStateAndBlocksResume()
    {
        // Arrange
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest: "broken");
        row.ScopeBackfilled = true;
        row.Status = DurableExecutionStatus.Running;
        row.WorkerId = "worker-a";
        row.LeaseEpoch = 1;
        row.LeaseExpiresAt = TimeProvider.System.GetUtcNow().AddMinutes(1);
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
    public async Task ValidateExecutionAsync_ConcurrentVersionChange_DoesNotOverwriteWinner()
    {
        // Arrange
        await using var database = await Database.CreateAsync();
        var row = CreateExecution(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest: "broken");
        row.ScopeBackfilled = true;
        database.Context.Add(row);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await database.Context.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER skip_quarantine BEFORE UPDATE OF state_version ON durable_execution
            BEGIN
                UPDATE durable_execution SET status = 4,
                    state_version = '11111111-1111-1111-1111-111111111111' WHERE id = OLD.id;
                SELECT RAISE(IGNORE);
            END;
            """,
            TestContext.Current.CancellationToken
        );

        // Act
        var valid = await database.Maintenance.ValidateExecutionAsync(row.Id, TestContext.Current.CancellationToken);

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
                        WorkspaceSnapshot = Agw.Shared.Utils.ProjectWorkspacePaths.CreateSnapshot(projectId, null),
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
                        Settings = DurableExecutionMapper.FromSettings(
                            SettingCommandMapper.FromCommand(new SettingCommand(projectId, conversationId))
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
            Maintenance = new DurableExecutionScopeMaintenance(Context, TimeProvider.System, Logger);
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
