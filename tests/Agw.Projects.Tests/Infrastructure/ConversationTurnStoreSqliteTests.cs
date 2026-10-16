using System.Diagnostics;
using System.Security.Claims;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Tests;

/// <summary>
/// 在 issue 的实际调用路径上检查 WAL：读取进行中的时候，Turn 状态的单行保存照常提交。
/// Checks WAL on the issue's actual call path: a single-row Turn status save commits while a read is in progress.
/// </summary>
public sealed class ConversationTurnStoreSqliteTests : IDisposable
{
    private readonly SqliteTestDatabase _database = SqliteTestDatabase.Create("turn-store");
    private readonly IDisposable _user = UserInfoUtil.Push(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
    );

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MarkRunningAsync_ActiveReaderOnWalDatabase_PersistsRunningStatus()
    {
        // Arrange
        var connectionString = _database.ConnectionString();
        Assert.Equal(_database.FilePath, SqliteJournalMode.EnsureWriteAheadLog(connectionString));
        Guid turnId;
        await using (var setup = SqliteTestDatabase.CreateContext(connectionString))
        {
            await setup.Database.MigrateAsync(CancellationToken);
            var conversationId = AddConversation(setup);
            turnId = AddAcceptedTurn(setup, conversationId, 0);
            AddAcceptedTurn(setup, conversationId, 2);
            await setup.SaveChangesAsync(CancellationToken);
        }

        await using var context = SqliteTestDatabase.CreateContext(connectionString);
        var store = new ConversationTurnStore(context, TimeProvider.System);
        await using var reader = SqliteLockHolder.Reader(
            connectionString,
            "project_conversation_turn",
            Timeout.InfiniteTimeSpan
        );
        var start = Stopwatch.GetTimestamp();

        // Act
        await store.MarkRunningAsync(turnId, CancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(start);

        // Assert: the reader is still active, and the save did not wait for a lock.
        // 断言：读取仍在进行，保存没有等待锁。
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"MarkRunningAsync took {elapsed}.");
        Assert.False(reader.Released.IsCompleted);
        await using var check = SqliteTestDatabase.CreateContext(connectionString);
        var turn = await check
            .ProjectConversationTurns.AsNoTracking()
            .SingleAsync(item => item.Id == turnId, CancellationToken);
        Assert.Equal(ProjectConversationTurnStatus.Running, turn.Status);
    }

    public void Dispose()
    {
        _user.Dispose();
        _database.Dispose();
    }

    private static Guid AddConversation(AgwDbContext context)
    {
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        context.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = projectId.ToString("N"),
                CreateBy = "tester",
            }
        );
        context.ProjectConversations.Add(
            new ProjectConversation
            {
                Id = conversationId,
                ProjectId = projectId,
                ContextId = conversationId.ToString("N"),
                CreateBy = "tester",
            }
        );
        return conversationId;
    }

    private static Guid AddAcceptedTurn(AgwDbContext context, Guid conversationId, long firstSequence)
    {
        var turnId = Guid.CreateVersion7();
        context.ProjectConversationTurns.Add(
            new ProjectConversationTurn
            {
                Id = turnId,
                ProjectConversationId = conversationId,
                TargetId = Guid.CreateVersion7(),
                RuntimeType = AgentRuntimeType.Agent,
                Status = ProjectConversationTurnStatus.Accepted,
                FirstSequence = firstSequence,
                StartedAt = DateTimeOffset.UnixEpoch,
            }
        );
        return turnId;
    }
}
