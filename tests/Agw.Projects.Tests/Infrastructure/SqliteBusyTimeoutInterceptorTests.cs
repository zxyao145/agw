using System.Diagnostics;
using System.Security.Claims;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Tests;

/// <summary>
/// 在真实的 SQLite 文件上检查每个操作的原生锁等待：上限等于这个操作的有效超时，reset 时的提交同样等待。
/// Checks each operation's native lock wait on real SQLite files: the limit is the operation's effective timeout, and the commit at reset waits as well.
/// </summary>
public sealed class SqliteBusyTimeoutInterceptorTests : IDisposable
{
    private const int SqliteBusy = 5;
    private static readonly TimeSpan WriterHold = TimeSpan.FromSeconds(5);

    private readonly SqliteTestDatabase _database = SqliteTestDatabase.Create("busy-timeout");
    private readonly IDisposable _user = UserInfoUtil.Push(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
    );

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("", null, 30000)]
    [InlineData("Default Timeout=5", null, 5000)]
    [InlineData("Default Timeout=0", null, int.MaxValue)]
    [InlineData("Default Timeout=5", 7, 7000)]
    public async Task SqlQueryRaw_EffectiveCommandTimeout_SetsBusyTimeout(
        string options,
        int? commandTimeout,
        int expected
    )
    {
        // Arrange
        await using var context = SqliteTestDatabase.CreateContext(_database.ConnectionString(options));
        if (commandTimeout is { } seconds)
            context.Database.SetCommandTimeout(seconds);

        // Act
        var actual = await context
            .Database.SqlQueryRaw<int>("SELECT timeout AS Value FROM pragma_busy_timeout()")
            .SingleAsync(CancellationToken);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task SaveChangesAsync_ActiveReaderOnRollbackJournal_CommitsAfterReaderCloses()
    {
        // Arrange
        var connectionString = _database.ConnectionString();
        var (first, _) = await CreateDatabaseAsync(connectionString, "delete");
        await using var context = SqliteTestDatabase.CreateContext(connectionString);
        var project = await context.Projects.SingleAsync(item => item.Id == first, CancellationToken);
        project.Description = "saved";
        await using var reader = SqliteLockHolder.Reader(connectionString, "project", TimeSpan.FromMilliseconds(300));

        // Act
        await context.SaveChangesAsync(CancellationToken);
        var savedAt = Stopwatch.GetTimestamp();

        // Assert
        Assert.True(savedAt > await reader.Released);
        Assert.Equal("saved", await ReadDescriptionAsync(connectionString, first));
    }

    [Fact]
    public async Task SaveChangesAsync_CommandTimeoutShorterThanDefaultTimeout_StopsWaitingAtCommandTimeout()
    {
        // Arrange
        var connectionString = _database.ConnectionString("Default Timeout=10");
        var (first, second) = await CreateDatabaseAsync(connectionString, "wal");
        await using var context = SqliteTestDatabase.CreateContext(connectionString);
        context.Database.SetCommandTimeout(1);
        var project = await context.Projects.SingleAsync(item => item.Id == first, CancellationToken);
        project.Description = "saved";
        await using var writer = SqliteLockHolder.Writer(connectionString, UpdateDescription(second), WriterHold);
        var start = Stopwatch.GetTimestamp();

        // Act
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(CancellationToken));
        var elapsed = Stopwatch.GetElapsedTime(start);

        // Assert
        Assert.Equal(SqliteBusy, Assert.IsType<SqliteException>(exception.InnerException).SqliteErrorCode);
        AssertStoppedAtOneSecond(elapsed);
    }

    [Fact]
    public async Task SaveChangesAsync_CommandTimeoutLongerThanDefaultTimeout_WaitsAtResetCommit()
    {
        // Arrange
        var connectionString = _database.ConnectionString("Default Timeout=1");
        var (first, _) = await CreateDatabaseAsync(connectionString, "delete");
        await using var context = SqliteTestDatabase.CreateContext(connectionString);
        context.Database.SetCommandTimeout(10);
        var project = await context.Projects.SingleAsync(item => item.Id == first, CancellationToken);
        project.Description = "saved";
        await using var reader = SqliteLockHolder.Reader(connectionString, "project", TimeSpan.FromSeconds(2));

        // Act
        await context.SaveChangesAsync(CancellationToken);
        var savedAt = Stopwatch.GetTimestamp();

        // Assert
        Assert.True(savedAt > await reader.Released);
        Assert.Equal("saved", await ReadDescriptionAsync(connectionString, first));
    }

    [Fact]
    public async Task BeginTransactionAsync_DefaultTimeoutChangedAfterOpen_StopsWaitingAtDefaultTimeout()
    {
        // Arrange
        var connectionString = _database.ConnectionString("Default Timeout=10");
        var (_, second) = await CreateDatabaseAsync(connectionString, "wal");
        await using var context = SqliteTestDatabase.CreateContext(connectionString);
        await using var writer = SqliteLockHolder.Writer(connectionString, UpdateDescription(second), WriterHold);
        await context.Database.OpenConnectionAsync(CancellationToken);
        ((SqliteConnection)context.Database.GetDbConnection()).DefaultTimeout = 1;
        var start = Stopwatch.GetTimestamp();

        // Act
        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.BeginTransactionAsync(CancellationToken)
        );
        var elapsed = Stopwatch.GetElapsedTime(start);

        // Assert
        Assert.Equal(SqliteBusy, exception.SqliteErrorCode);
        AssertStoppedAtOneSecond(elapsed);
    }

    [Fact]
    public async Task SaveChangesAsync_AutoTransactionWithDefaultTimeoutChangedAfterOpen_StopsWaitingAtDefaultTimeout()
    {
        // Arrange: two modified rows make SaveChanges start its own transaction.
        // 准备：修改两行，SaveChanges 自动开启事务。
        var connectionString = _database.ConnectionString("Default Timeout=10");
        var (_, second) = await CreateDatabaseAsync(connectionString, "wal");
        await using var context = SqliteTestDatabase.CreateContext(connectionString);
        foreach (var project in await context.Projects.ToListAsync(CancellationToken))
            project.Description = "saved";
        await using var writer = SqliteLockHolder.Writer(connectionString, UpdateDescription(second), WriterHold);
        await context.Database.OpenConnectionAsync(CancellationToken);
        ((SqliteConnection)context.Database.GetDbConnection()).DefaultTimeout = 1;
        var start = Stopwatch.GetTimestamp();

        // Act
        var exception = await Assert.ThrowsAsync<SqliteException>(() => context.SaveChangesAsync(CancellationToken));
        var elapsed = Stopwatch.GetElapsedTime(start);

        // Assert
        Assert.Equal(SqliteBusy, exception.SqliteErrorCode);
        AssertStoppedAtOneSecond(elapsed);
    }

    public void Dispose()
    {
        _user.Dispose();
        _database.Dispose();
    }

    /// <summary>
    /// 按指定 journal mode 创建数据库、完成 migration，并写入两个 Project。
    /// Creates the database in the given journal mode, applies migrations and writes two Projects.
    /// </summary>
    private async Task<(Guid First, Guid Second)> CreateDatabaseAsync(string connectionString, string journalMode)
    {
        Assert.Equal(journalMode, _database.ExecuteScalar($"PRAGMA journal_mode = {journalMode}"));
        await using var context = SqliteTestDatabase.CreateContext(connectionString);
        await context.Database.MigrateAsync(CancellationToken);
        var first = AddProject(context);
        var second = AddProject(context);
        await context.SaveChangesAsync(CancellationToken);
        Assert.Equal(journalMode, _database.ReadJournalMode());
        return (first, second);
    }

    private static Guid AddProject(AgwDbContext context)
    {
        var id = Guid.CreateVersion7();
        context.Projects.Add(
            new Project
            {
                Id = id,
                Name = id.ToString("N"),
                CreateBy = "tester",
            }
        );
        return id;
    }

    private static async Task<string?> ReadDescriptionAsync(string connectionString, Guid projectId)
    {
        await using var context = SqliteTestDatabase.CreateContext(connectionString);
        var project = await context
            .Projects.AsNoTracking()
            .SingleAsync(item => item.Id == projectId, CancellationToken);
        return project.Description;
    }

    // Project 的名称就是它的 ID，原始 SQL 按名称定位行。
    // A Project's name is its ID, so raw SQL locates the row by name.
    private static string UpdateDescription(Guid projectId) =>
        $"UPDATE project SET description = 'held' WHERE name = '{projectId:N}'";

    private static void AssertStoppedAtOneSecond(TimeSpan elapsed)
    {
        Assert.True(elapsed >= TimeSpan.FromSeconds(1), $"Stopped waiting after {elapsed}.");
        Assert.True(elapsed < WriterHold, $"Waited {elapsed}, until the writer released its lock.");
    }
}
