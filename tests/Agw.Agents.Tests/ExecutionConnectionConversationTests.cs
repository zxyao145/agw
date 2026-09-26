using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Commands.Checkpoint;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public partial class ExecutionCommandHandlerTests
{
    [Fact]
    public async Task StartTurnAsync_OtherConversationThanConfigured_ThrowsInvalidParam()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("configured-context");
        await using var context = CreateContext(task);
        var configuredConversationId = Guid.CreateVersion7();
        await context.ApplySettingsAsync(
            SettingCommandMapper.FromCommand(new SettingCommand(task.ProjectId, configuredConversationId)),
            token
        );

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.StartTurnAsync(CreateExecCommand(Guid.CreateVersion7()), token)
        );

        // Assert
        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Empty(Runtimes.TurnContexts);
        Assert.Equal(configuredConversationId, context.ConversationId);
    }

    [Fact]
    public async Task StartTurnAsync_RunningTurn_ThrowsBusyWithoutErrorMessage()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var sink = new CapturingSink();
        Runtimes.HoldTurnOpen = true;
        await using var context = CreateContext(CreateTask("busy-context"), sink: sink);
        var first = CreateExecCommand(Guid.CreateVersion7());
        await context.StartTurnAsync(first, token);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, token);

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.StartTurnAsync(CreateNextCommand(first), token)
        );

        // Assert
        Assert.Equal(ErrorCodes.ExecutionBusy.Code, exception.Code);
        Assert.DoesNotContain(sink.Messages, message => message.Contents.Any(content => content is AgwErrorContent));
        Runtimes.ReleaseHeldTurns();
        await context.WhenIdleAsync();
        Assert.Single(Runtimes.TurnContexts);
    }

    [Fact]
    public async Task StartTurnAsync_AfterFinishMessageBeforeTurnSettles_AcceptsNextTurn()
    {
        // Arrange: the client has received the finish message while the turn task is still settling.
        // 准备：客户端已经收到结束消息，Turn 任务仍在收尾。
        var token = TestContext.Current.CancellationToken;
        var sink = new FinishGateSink();
        await using var context = CreateContext(CreateTask("settling-context"), sink: sink);
        var first = CreateExecCommand(Guid.CreateVersion7());
        Task next;
        try
        {
            await context.StartTurnAsync(first, token);
            await sink.FirstFinish.WaitAsync(TurnTimeout, token);
            Assert.True(context.HasActiveTurn);

            // Act: the next turn arrives at once and waits for the settling turn.
            // 执行：下一轮立即到达，等待正在收尾的 Turn。
            next = context.StartTurnAsync(CreateNextCommand(first), token);
            Assert.False(next.IsCompleted);
        }
        finally
        {
            sink.ReleaseFinish();
        }
        await next.WaitAsync(TurnTimeout, token);
        await context.WhenIdleAsync();

        // Assert
        Assert.Equal(2, Runtimes.TurnContexts.Count);
        Assert.Equal(2, sink.Messages.Count(AgwMessageClassifier.IsTurnStart));
        Assert.All(
            sink.Messages.Where(AgwMessageClassifier.IsTurnFinished),
            message => Assert.Equal("completed", message.AdditionalProperties!["status"])
        );
    }

    [Fact]
    public async Task GetAgentflowCheckpointsAsync_DraftConversation_ReturnsEmpty()
    {
        // Arrange: a configured conversation that no turn has saved yet.
        // 准备：已配置、但还没有 Turn 保存的对话。
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("draft-context");
        await using var context = CreateContext(task, checkpointStore: CreateUnusedCheckpointStore());
        await context.ApplySettingsAsync(
            SettingCommandMapper.FromCommand(new SettingCommand(task.ProjectId, Guid.CreateVersion7())),
            token
        );

        // Act
        var checkpoints = await context.GetAgentflowCheckpointsAsync(Guid.CreateVersion7(), token);

        // Assert
        Assert.Empty(checkpoints);
    }

    [Fact]
    public async Task ResumeCheckpointAsync_DraftConversation_ThrowsResourceNotFound()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("draft-context");
        await using var context = CreateContext(task, checkpointStore: CreateUnusedCheckpointStore());
        await context.ApplySettingsAsync(
            SettingCommandMapper.FromCommand(new SettingCommand(task.ProjectId, Guid.CreateVersion7())),
            token
        );

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.ResumeCheckpointAsync(
                new ResumeCheckpointCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()),
                token
            )
        );

        // Assert
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        Assert.Empty(Runtimes.TurnContexts);
    }

    /// <summary>
    /// 草稿对话在查询数据库之前就返回，因此 checkpoint 存储只需要能构造。
    /// A draft conversation returns before any database query, so the checkpoint store only needs to be constructible.
    /// </summary>
    private static AgentflowCheckpointStore CreateUnusedCheckpointStore() =>
        new(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            InMemoryApplicationLock.Shared,
            TimeProvider.System
        );
}
