using System.Threading.Channels;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Turns;
using Agw.Testing;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

/// <summary>
/// TurnBroadcast 的进程内增量合并、写入顺序、回放游标与订阅者管理。
/// In-process delta merging, write order, replay cursors and subscriber management of TurnBroadcast.
/// </summary>
public sealed class TurnBroadcastTests
{
    private readonly ManualTimeProvider _clock = new();
    private readonly TurnBroadcast _broadcast;
    private readonly RecordingSink _sink = new();

    public TurnBroadcastTests()
    {
        _broadcast = new TurnBroadcast(Guid.CreateVersion7(), "user-1", _clock);
        _broadcast.AddSink(_sink);
    }

    [Fact]
    public async Task WriteAsync_DeltasWithinWindow_PublishOneMergedMessageWhenTheWindowEnds()
    {
        var token = TestContext.Current.CancellationToken;
        await _broadcast.WriteAsync(Delta("He"), token);
        await _broadcast.WriteAsync(Delta("ll"), token);
        await _broadcast.WriteAsync(Delta("o"), token);
        Assert.Empty(_sink.Messages);

        _clock.Advance(TurnBroadcast.CoalescingWindow);
        var message = await _sink.ReadAsync(token);

        Assert.Equal("Hello", TextOf(message));
        Assert.Equal(1L, message.AdditionalProperties?[TurnMessageFactory.TurnSequenceKey]);
        Assert.Equal(1, _broadcast.LastSequence);
    }

    [Fact]
    public async Task WriteAsync_OtherMessage_PublishesPendingDeltaFirst()
    {
        var token = TestContext.Current.CancellationToken;
        await _broadcast.WriteAsync(Delta("partial"), token);

        await _broadcast.WriteAsync(Control(), token);

        Assert.Collection(
            _sink.Messages,
            message => Assert.Equal("partial", TextOf(message)),
            message => Assert.Equal(AgwMessageTypes.InteractionRequest, AgwMessageClassifier.GetMessageType(message))
        );
        Assert.Equal([1L, 2L], _sink.Messages.Select(Sequence));
    }

    [Fact]
    public async Task WriteAsync_FinishMessage_PublishesPendingDeltaAndFinishes()
    {
        var token = TestContext.Current.CancellationToken;
        var finished = 0;
        _broadcast.Finished += _ => finished++;
        await _broadcast.WriteAsync(Delta("last words"), token);

        await _broadcast.WriteAsync(TurnMessageFactory.CreateFinished(AgwTurnStatus.Completed), token);
        _clock.Advance(TurnBroadcast.CoalescingWindow);

        Assert.Equal(["last words", ""], _sink.Messages.Select(TextOf));
        Assert.True(_broadcast.IsFinished);
        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task AttachAsync_Cursor_ReplaysOnlyLaterMessagesAndReceivesNewOnes()
    {
        var token = TestContext.Current.CancellationToken;
        await _broadcast.WriteAsync(Control(), token);
        await _broadcast.WriteAsync(Control(), token);
        await _broadcast.WriteAsync(Control(), token);
        var replay = new RecordingSink();

        await _broadcast.AttachAsync(replay, afterSequence: 1);
        await _broadcast.WriteAsync(Control(), token);

        Assert.Equal([2L, 3L, 4L], replay.Messages.Select(Sequence));
    }

    [Fact]
    public void ReadAfter_CommittedEntries_ReturnsContiguousTailAndWakesObservers()
    {
        _broadcast.PublishCommitted([Entry(1), Entry(2), Entry(3)]);

        var tail = _broadcast.ReadAfter(1, out var changed);
        var none = _broadcast.ReadAfter(3, out _);
        _broadcast.PublishCommitted([Entry(4)]);

        Assert.Equal([2L, 3L], tail.Select(entry => entry.Sequence));
        Assert.Empty(none);
        Assert.True(changed.IsCompleted);
        Assert.All(_broadcast.ReadAfter(0, out _), entry => Assert.Null(entry.PayloadJson));
    }

    [Fact]
    public void ReadAfter_GapInCommittedEntries_ReturnsNothingBeforeTheGap()
    {
        _broadcast.PublishCommitted([Entry(1), Entry(2)]);
        _broadcast.PublishCommitted([Entry(5), Entry(6)]);

        Assert.Empty(_broadcast.ReadAfter(2, out _));
        Assert.Equal([6L], _broadcast.ReadAfter(5, out _).Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task RemoveSink_RemovedSubscriber_ReceivesNoLaterMessages()
    {
        var token = TestContext.Current.CancellationToken;
        var other = new RecordingSink();
        _broadcast.AddSink(other);
        await _broadcast.WriteAsync(Control(), token);

        _broadcast.RemoveSink(_sink);
        await _broadcast.WriteAsync(Control(), token);

        Assert.Single(_sink.Messages);
        Assert.Equal(2, other.Messages.Count);
    }

    private static AgwMessage Delta(string text) =>
        new(
            "message-1",
            "agent",
            AiRole.Assistant,
            [
                new AgwTextContent
                {
                    Content = text,
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["blockId"] = "block:0" },
                },
            ]
        );

    private static AgwMessage Control() =>
        new(
            Guid.CreateVersion7().ToString("D"),
            "agent",
            AiRole.Assistant,
            [],
            new AdditionalPropertiesDictionary { [AgwMessageClassifier.TypeKey] = AgwMessageTypes.InteractionRequest }
        );

    private static TurnBroadcastEntry Entry(long sequence) => new(sequence, Control(), "{}");

    private static string TextOf(AgwMessage message) =>
        string.Concat(message.Contents.OfType<AgwTextContent>().Select(content => content.Content));

    private static long Sequence(AgwMessage message) =>
        (long)message.AdditionalProperties![TurnMessageFactory.TurnSequenceKey]!;

    private sealed class RecordingSink : IExecutionMessageSink
    {
        private readonly Channel<AgwMessage> _written = Channel.CreateUnbounded<AgwMessage>();

        public List<AgwMessage> Messages { get; } = [];

        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
        {
            lock (Messages)
                Messages.Add(message);
            return _written.Writer.WriteAsync(message, cancellationToken);
        }

        public ValueTask<AgwMessage> ReadAsync(CancellationToken cancellationToken) =>
            _written.Reader.ReadAsync(cancellationToken);
    }
}
