using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Tests;

public class TurnPipelineTests
{
    [Fact]
    public async Task RunAsync_Streaming_EmitsStartContentAndCompletedFinish()
    {
        var sink = new CapturingSink();
        var content = CreateMessage("content");

        await TurnPipeline.RunAsync(
            ToStream(content, cancellationToken: TestContext.Current.CancellationToken),
            true,
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["turn-start", null, "turn-finished"], sink.Messages.Select(GetType));
        Assert.Equal("completed", sink.Messages[^1].AdditionalProperties!["status"]);
    }

    [Fact]
    public async Task RunAsync_NonStreaming_ForwardsHumanGateBeforeBufferedContent()
    {
        var sink = new CapturingSink();
        var content = CreateMessage("content");
        var gate = CreateMessage("gate", "human-gate-request");

        await TurnPipeline.RunAsync(
            ToStream(content, gate, TestContext.Current.CancellationToken),
            false,
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["turn-start", "human-gate-request", null, "turn-finished"], sink.Messages.Select(GetType));
    }

    [Fact]
    public async Task RunAsync_NonStreaming_ForwardsToolApprovalBeforeBufferedContent()
    {
        var sink = new CapturingSink();
        var content = CreateMessage("content");
        var approval = CreateMessage("approval", "tool-approval-request");

        await TurnPipeline.RunAsync(
            ToStream(content, approval, TestContext.Current.CancellationToken),
            false,
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["turn-start", "tool-approval-request", null, "turn-finished"], sink.Messages.Select(GetType));
    }

    [Fact]
    public async Task RunAsync_NonStreaming_ForwardsCheckpointBeforeBufferedContent()
    {
        var sink = new CapturingSink();
        var content = CreateMessage("content");
        var checkpoint = CreateMessage("saved", "agentflow-checkpoint");

        await TurnPipeline.RunAsync(
            ToStream(content, checkpoint, TestContext.Current.CancellationToken),
            false,
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["turn-start", "agentflow-checkpoint", null, "turn-finished"], sink.Messages.Select(GetType));
    }

    [Fact]
    public async Task RunAsync_FatalErrorContent_EmitsFailedFinish()
    {
        var sink = new CapturingSink();
        var error = CreateErrorMessage("model unavailable", fatal: true);

        await TurnPipeline.RunAsync(
            ToStream(error, cancellationToken: TestContext.Current.CancellationToken),
            true,
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Same(error, sink.Messages[1]);
        Assert.Equal("failed", sink.Messages[^1].AdditionalProperties!["status"]);
    }

    [Fact]
    public async Task RunAsync_RecoverableErrorContent_EmitsCompletedFinish()
    {
        var sink = new CapturingSink();
        var error = CreateErrorMessage("tool failed", fatal: false);

        await TurnPipeline.RunAsync(
            ToStream(error, cancellationToken: TestContext.Current.CancellationToken),
            true,
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Same(error, sink.Messages[1]);
        Assert.Equal("completed", sink.Messages[^1].AdditionalProperties!["status"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_FatalErrorThenException_DoesNotEmitDuplicateError(bool stream)
    {
        var sink = new CapturingSink();
        var error = CreateErrorMessage("model unavailable", fatal: true);

        await TurnPipeline.RunAsync(
            ThrowingStream(error, TestContext.Current.CancellationToken),
            stream,
            sink,
            TestContext.Current.CancellationToken
        );

        Assert.Single(sink.Messages.SelectMany(message => message.Contents).OfType<AgwErrorContent>());
        Assert.Equal("failed", sink.Messages[^1].AdditionalProperties!["status"]);
    }

    [Fact]
    public async Task RunAsync_ExceptionWithoutErrorContent_EmitsSyntheticError()
    {
        var sink = new CapturingSink();

        await TurnPipeline.RunAsync(
            ThrowingStream(null, TestContext.Current.CancellationToken),
            true,
            sink,
            TestContext.Current.CancellationToken
        );

        var error = Assert.IsType<AgwErrorContent>(Assert.Single(sink.Messages[1].Contents));
        Assert.Equal("stream failed", error.Content);
        Assert.Equal("failed", sink.Messages[^1].AdditionalProperties!["status"]);
    }

    [Fact]
    public async Task RunAsync_LazyNonStreamingExecutionFails_EmitsSyntheticErrorAndFinish()
    {
        var sink = new CapturingSink();
        var messages = RuntimeFactory.ToAsyncEnumerable(() =>
            Task.FromException<IReadOnlyList<AgwMessage>>(new InvalidOperationException("non-streaming failed"))
        );

        Assert.Empty(sink.Messages);

        await TurnPipeline.RunAsync(messages, false, sink, TestContext.Current.CancellationToken);

        var error = Assert.IsType<AgwErrorContent>(Assert.Single(sink.Messages[1].Contents));
        Assert.Equal("non-streaming failed", error.Content);
        Assert.Equal("failed", sink.Messages[^1].AdditionalProperties!["status"]);
    }

    private static async IAsyncEnumerable<AgwMessage> ToStream(
        AgwMessage first,
        AgwMessage? second = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        yield return first;
        if (second != null)
            yield return second;
        await Task.CompletedTask;
    }

    private static AgwMessage CreateMessage(string content, string? type = null)
    {
        var properties =
            type == null ? null : new Microsoft.Extensions.AI.AdditionalPropertiesDictionary { ["type"] = type };
        return new AgwMessage(
            Guid.CreateVersion7().ToString("D"),
            Agw.Shared.Constants.DefaultAgentAuthor,
            AiRole.Assistant,
            [new AgwTextContent { Content = content }],
            properties
        );
    }

    private static AgwMessage CreateErrorMessage(string content, bool fatal)
    {
        var properties = fatal
            ? new Microsoft.Extensions.AI.AdditionalPropertiesDictionary { ["isFatalError"] = true }
            : null;
        return new AgwMessage(
            Guid.CreateVersion7().ToString("D"),
            Agw.Shared.Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = content, AdditionalProperties = properties }]
        );
    }

    private static async IAsyncEnumerable<AgwMessage> ThrowingStream(
        AgwMessage? first,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (first != null)
        {
            yield return first;
        }

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("stream failed");
    }

    private static string? GetType(AgwMessage message) =>
        message.AdditionalProperties?.TryGetValue("type", out var value) == true ? value as string : null;

    private sealed class CapturingSink : IExecutionMessageSink
    {
        public List<AgwMessage> Messages { get; } = [];

        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return ValueTask.CompletedTask;
        }
    }
}
