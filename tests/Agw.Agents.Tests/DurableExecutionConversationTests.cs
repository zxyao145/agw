using System.Threading.Channels;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    [Fact]
    public async Task AttachAsync_ExecutionOfOtherConversation_ThrowsNotFound()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var executionId = (await AcceptAsync(task)).Request.TurnId;
        var sink = new FinishObservingSink();
        await using var attachment = new DurableExecutionAttachment(UserId, sink, token, _kit.Coordinator);

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            attachment.AttachAsync(executionId, cursor: null, Guid.CreateVersion7(), token)
        );

        // Assert
        Assert.Equal(ErrorCodes.DurableExecutionNotFound.Code, exception.Code);
        Assert.False(attachment.HasActiveExecution);
    }

    [Fact]
    public async Task InterruptAsync_UnattachedExecutionOfOtherConversation_ThrowsNotFoundAndKeepsRunning()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var executionId = (await AcceptAsync(task)).Request.TurnId;
        await using var attachment = new DurableExecutionAttachment(
            UserId,
            new FinishObservingSink(),
            token,
            _kit.Coordinator
        );

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            attachment.InterruptAsync(executionId, reason: null, Guid.CreateVersion7(), token)
        );

        // Assert
        Assert.Equal(ErrorCodes.DurableExecutionNotFound.Code, exception.Code);
        var status = await _kit.Coordinator.GetStatusAsync(executionId, UserId, token);
        Assert.False(DurableExecutionQueries.IsTerminal(status.Status));
    }

    [Fact]
    public async Task PumpAsync_FinishMessage_ClearsActiveExecutionBeforeClientReceivesIt()
    {
        // Arrange: attach to an execution that has not finished yet.
        // 准备：附着到一个还没有结束的执行。
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var executionId = (await AcceptAsync(task)).Request.TurnId;
        var sink = new FinishObservingSink();
        await using var attachment = new DurableExecutionAttachment(UserId, sink, token, _kit.Coordinator);
        sink.Attachment = attachment;
        await attachment.AttachAsync(executionId, cursor: null, task.ProjectConversationId, token);
        Assert.True(attachment.HasActiveExecution);

        // Act
        Assert.True(await _kit.Coordinator.InterruptAsync(executionId, UserId, reason: null, token));
        var activeWhenFinishArrived = await sink.ActiveWhenFinished.Task.WaitAsync(TimeSpan.FromSeconds(10), token);

        // Assert
        Assert.False(activeWhenFinishArrived);
    }

    /// <summary>
    /// 记录收到结束消息那一刻连接是否仍有活动执行。
    /// Records whether the connection still had an active execution at the moment the finish message arrived.
    /// </summary>
    private sealed class FinishObservingSink : IExecutionMessageSink
    {
        private readonly Channel<AgwMessage> _messages = Channel.CreateUnbounded<AgwMessage>();

        public DurableExecutionAttachment? Attachment { get; set; }

        public TaskCompletionSource<bool> ActiveWhenFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
        {
            if (AgwMessageClassifier.IsTurnFinished(message) && Attachment != null)
            {
                ActiveWhenFinished.TrySetResult(Attachment.HasActiveExecution);
            }
            return _messages.Writer.WriteAsync(message, cancellationToken);
        }
    }
}
