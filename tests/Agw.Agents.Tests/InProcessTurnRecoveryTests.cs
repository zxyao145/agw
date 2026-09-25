using Agw.Infrastructure.Projects;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

/// <summary>
/// 进程内模式的启动修复：上次进程残留的活动 Turn 结束为 Interrupted，有活动 durable_execution 的 Turn 与已结束的 Turn 保持不变。
/// In-process startup recovery: active turns left by the previous process finish as Interrupted, while turns with an active durable_execution and finished turns stay unchanged.
/// </summary>
public sealed class InProcessTurnRecoveryTests : IAsyncLifetime
{
    private readonly DateTimeOffset _started = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private TurnPersistenceTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await TurnPersistenceTestKit.CreateAsync();

    public ValueTask DisposeAsync() => _kit.DisposeAsync();

    [Fact]
    public async Task RecoverAsync_ActiveTurnsWithoutActiveDurableExecution_FinishAsInterrupted()
    {
        // Arrange: turns of two owners in every relevant state.
        // 准备：两个用户的对话中各种状态的 Turn。
        var token = TestContext.Current.CancellationToken;
        var conversationId = (await _kit.SeedConversationAsync()).ProjectConversationId;
        var foreignConversationId = (
            await _kit.SeedConversationAsync(userId: "another-user", contextId: "context-2")
        ).ProjectConversationId;
        var accepted = await AddTurnAsync(conversationId, ProjectConversationTurnStatus.Accepted, 0);
        var running = await AddTurnAsync(conversationId, ProjectConversationTurnStatus.Running, 1, historyRows: 3);
        var durable = await AddTurnAsync(
            conversationId,
            ProjectConversationTurnStatus.Running,
            4,
            durableStatus: DurableExecutionStatus.Running
        );
        var finishedDurable = await AddTurnAsync(
            conversationId,
            ProjectConversationTurnStatus.Running,
            5,
            durableStatus: DurableExecutionStatus.Completed
        );
        var completed = await AddTurnAsync(conversationId, ProjectConversationTurnStatus.Completed, 6);
        var failed = await AddTurnAsync(conversationId, ProjectConversationTurnStatus.Failed, 7);
        var foreign = await AddTurnAsync(foreignConversationId, ProjectConversationTurnStatus.Running, 0);
        var recovery = new InProcessTurnRecovery(_kit.Services.GetRequiredService<IServiceScopeFactory>());

        // Act
        await recovery.RecoverAsync(token);

        // Assert
        foreach (var turnId in new[] { accepted, running, finishedDurable, foreign })
        {
            var turn = await _kit.ReadTurnAsync(turnId);
            Assert.Equal(ProjectConversationTurnStatus.Interrupted, turn.Status);
            Assert.NotNull(turn.FinishedAt);
        }
        Assert.Equal(3, (await _kit.ReadTurnAsync(running)).LastSequence);
        var durableTurn = await _kit.ReadTurnAsync(durable);
        Assert.Equal(ProjectConversationTurnStatus.Running, durableTurn.Status);
        Assert.Null(durableTurn.FinishedAt);
        Assert.Equal(ProjectConversationTurnStatus.Completed, (await _kit.ReadTurnAsync(completed)).Status);
        Assert.Equal(_started, (await _kit.ReadTurnAsync(completed)).FinishedAt);
        Assert.Equal(ProjectConversationTurnStatus.Failed, (await _kit.ReadTurnAsync(failed)).Status);
        Assert.Equal(_started, (await _kit.ReadTurnAsync(failed)).FinishedAt);
    }

    /// <summary>
    /// 写入一个 Turn；historyRows 为本 Turn 从 firstSequence 开始的消息行数，durableStatus 为同 ID 的执行记录状态。
    /// Writes one turn; historyRows is the number of its message rows from firstSequence, and durableStatus the status of the execution record with the same ID.
    /// </summary>
    private async Task<Guid> AddTurnAsync(
        Guid conversationId,
        ProjectConversationTurnStatus status,
        long firstSequence,
        int historyRows = 0,
        DurableExecutionStatus? durableStatus = null
    )
    {
        var turnId = Guid.CreateVersion7();
        var finished =
            status
            is ProjectConversationTurnStatus.Completed
                or ProjectConversationTurnStatus.Failed
                or ProjectConversationTurnStatus.Interrupted;
        await using var context = _kit.CreateContext();
        context.ProjectConversationTurns.Add(
            new ProjectConversationTurn
            {
                Id = turnId,
                ProjectConversationId = conversationId,
                TargetId = Guid.CreateVersion7(),
                RuntimeType = AgentRuntimeType.Agent,
                Status = status,
                FirstSequence = firstSequence,
                StartedAt = _started,
                FinishedAt = finished ? _started : null,
            }
        );
        for (var index = 0; index < historyRows; index++)
            context.ProjectConversationChatHistories.Add(
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = conversationId,
                    TaskId = turnId,
                    ConversationSequence = firstSequence + index,
                    TurnId = turnId,
                    Purpose = ConversationMessagePurpose.Message,
                    ConversationPayload = "{}",
                    CreateTime = _started,
                }
            );
        if (durableStatus is { } executionStatus)
            context.DurableExecutions.Add(
                new DurableExecutionRecord
                {
                    Id = turnId,
                    UserId = TurnPersistenceTestKit.UserId,
                    ManifestJson = "{}",
                    Status = executionStatus,
                    StateChangedAt = _started,
                    StateVersion = Guid.CreateVersion7(),
                }
            );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return turnId;
    }
}
