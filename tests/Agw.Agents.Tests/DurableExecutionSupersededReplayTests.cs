using System.Threading.Channels;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Turns;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    [Fact]
    public async Task AttachAsync_TurnSupersededByNewerTurn_MarksReplayedLifecycleMessages()
    {
        // Arrange: an interrupted turn, replayed once before and once after a newer turn joins the conversation.
        // 准备：一个被中断的 Turn，在会话出现更新的 Turn 之前和之后各回放一次。
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var earlier = (await AcceptAsync(task)).Request.TurnId;
        Assert.True(await _kit.Coordinator.InterruptAsync(earlier, UserId, reason: null, token));
        var conversationId = task.ProjectConversationId;
        var beforeNewer = await ReplayAsync(earlier, conversationId, cursor: null, untilFinished: true);
        var later = (await AcceptAsync(task)).Request.TurnId;

        // Act: a replay from the start reads the event log; one after the start event reads this instance's broadcast buffer.
        // 执行：从头回放读取事件记录；从开始事件之后回放读取本实例的广播缓冲。
        var fromEventLog = await ReplayAsync(earlier, conversationId, cursor: null, untilFinished: true);
        var fromBroadcast = await ReplayAsync(earlier, conversationId, cursor: "1", untilFinished: true);
        var latest = await ReplayAsync(later, conversationId, cursor: null, untilFinished: false);

        // Assert
        Assert.Equal([false, false], beforeNewer.Select(IsSuperseded));
        Assert.Equal([true, true], fromEventLog.Select(IsSuperseded));
        Assert.True(AgwMessageClassifier.IsTurnStart(fromEventLog[0]));
        Assert.True(AgwMessageClassifier.IsTurnFinished(fromEventLog[1]));
        Assert.True(IsSuperseded(Assert.Single(fromBroadcast)));
        Assert.False(IsSuperseded(Assert.Single(latest)));
        // 事件记录与广播缓冲中的原消息保持不变。The original messages in the event log and broadcast buffer stay unchanged.
        Assert.All(
            await _kit.ReadEventsAsync(earlier),
            entry => Assert.False(IsSuperseded(DurableExecutionEvents.ToEntry(entry).Message))
        );
        var buffered = Assert.Single(_kit.Broadcasts.Find(earlier, UserId)!.ReadAfter(1, out _));
        Assert.False(IsSuperseded(buffered.Message));
    }

    private static bool IsSuperseded(AgwMessage message) =>
        message.AdditionalProperties?.TryGetValue(TurnMessageFactory.SupersededKey, out var value) == true
        && value is true;

    /// <summary>
    /// 从游标之后回放一个执行：到结束消息为止，或只取第一条消息。
    /// Replays an execution after a cursor: up to its finish message, or only the first message.
    /// </summary>
    private async Task<List<AgwMessage>> ReplayAsync(
        Guid executionId,
        Guid conversationId,
        string? cursor,
        bool untilFinished
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var sink = new ReplaySink();
        await using var attachment = new DurableExecutionAttachment(UserId, sink, token, _kit.Coordinator);
        await attachment.AttachAsync(executionId, cursor, conversationId, token);
        var messages = new List<AgwMessage>();
        await foreach (var message in sink.ReadAllAsync(token))
        {
            messages.Add(message);
            if (!untilFinished || AgwMessageClassifier.IsTurnFinished(message))
                break;
        }
        return messages;
    }

    private sealed class ReplaySink : IExecutionMessageSink
    {
        private readonly Channel<AgwMessage> _messages = Channel.CreateUnbounded<AgwMessage>();

        public IAsyncEnumerable<AgwMessage> ReadAllAsync(CancellationToken cancellationToken) =>
            _messages.Reader.ReadAllAsync(cancellationToken);

        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken) =>
            _messages.Writer.WriteAsync(message, cancellationToken);
    }
}
