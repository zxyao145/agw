using System.Reflection;
using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Turns;

namespace Agw.Agents.Tests;

public class TurnPipelineTests
{
    private static readonly TurnEnvelope Envelope = new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        AgentRuntimeType.Agent,
        "input-1"
    );

    [Fact]
    public async Task RunAsync_Streaming_EmitsContentAndCompletedFinishWithTurnFields()
    {
        var sink = new CapturingSink();
        var content = CreateMessage("content");

        await RunAsync(ToStream(content, cancellationToken: TestContext.Current.CancellationToken), true, sink);

        Assert.Equal([null, AgwMessageTypes.TurnFinished], sink.Messages.Select(GetType));
        var finished = sink.Messages[^1].AdditionalProperties!;
        Assert.Equal(AgwTurnStatus.Completed, finished["status"]);
        Assert.Equal(Envelope.TurnId.ToString("D"), finished["turnId"]);
        Assert.Equal(Envelope.ConversationId.ToString("D"), finished["conversationId"]);
        Assert.Equal(Envelope.AgentId.ToString("D"), finished["agentId"]);
        Assert.Equal("agent", finished["agentType"]);
        Assert.Equal("input-1", finished["streamingScopeId"]);
        Assert.Equal(0, finished["stepCount"]);
        Assert.False(finished.ContainsKey("errorCode"));
    }

    [Fact]
    public async Task RunAsync_NonStreaming_ForwardsHumanGateBeforeBufferedContent()
    {
        var sink = new CapturingSink();
        var content = CreateMessage("content");
        var gate = CreateMessage("gate", "human-gate-request");

        await RunAsync(ToStream(content, gate, TestContext.Current.CancellationToken), false, sink);

        Assert.Equal(["human-gate-request", null, AgwMessageTypes.TurnFinished], sink.Messages.Select(GetType));
    }

    [Fact]
    public async Task RunAsync_NonStreaming_ForwardsToolApprovalBeforeBufferedContent()
    {
        var sink = new CapturingSink();
        var content = CreateMessage("content");
        var approval = CreateMessage("approval", "tool-approval-request");

        await RunAsync(ToStream(content, approval, TestContext.Current.CancellationToken), false, sink);

        Assert.Equal(["tool-approval-request", null, AgwMessageTypes.TurnFinished], sink.Messages.Select(GetType));
    }

    [Fact]
    public async Task RunAsync_NonStreaming_ForwardsCheckpointBeforeBufferedContent()
    {
        var sink = new CapturingSink();
        var content = CreateMessage("content");
        var checkpoint = CreateMessage("saved", AgwMessageTypes.AgentflowCheckpoint);

        await RunAsync(ToStream(content, checkpoint, TestContext.Current.CancellationToken), false, sink);

        Assert.Equal(
            [AgwMessageTypes.AgentflowCheckpoint, null, AgwMessageTypes.TurnFinished],
            sink.Messages.Select(GetType)
        );
    }

    [Fact]
    public async Task RunAsync_FatalErrorContent_EmitsFailedFinishWithErrorCode()
    {
        var sink = new CapturingSink();
        var error = CreateErrorMessage("model unavailable", fatal: true);

        await RunAsync(ToStream(error, cancellationToken: TestContext.Current.CancellationToken), true, sink);

        Assert.Same(error, sink.Messages[0]);
        Assert.Equal(AgwTurnStatus.Failed, sink.Messages[^1].AdditionalProperties!["status"]);
        Assert.NotNull(sink.Messages[^1].AdditionalProperties!["errorCode"]);
    }

    [Fact]
    public async Task RunAsync_RecoverableErrorContent_EmitsCompletedFinish()
    {
        var sink = new CapturingSink();
        var error = CreateErrorMessage("tool failed", fatal: false);

        await RunAsync(ToStream(error, cancellationToken: TestContext.Current.CancellationToken), true, sink);

        Assert.Same(error, sink.Messages[0]);
        Assert.Equal(AgwTurnStatus.Completed, sink.Messages[^1].AdditionalProperties!["status"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_FatalErrorThenException_DoesNotEmitDuplicateError(bool stream)
    {
        var sink = new CapturingSink();
        var error = CreateErrorMessage("model unavailable", fatal: true);

        await RunAsync(ThrowingStream(error, TestContext.Current.CancellationToken), stream, sink);

        Assert.Single(sink.Messages.SelectMany(message => message.Contents).OfType<AgwErrorContent>());
        Assert.Equal(AgwTurnStatus.Failed, sink.Messages[^1].AdditionalProperties!["status"]);
    }

    [Fact]
    public async Task RunAsync_ExceptionWithoutErrorContent_EmitsSyntheticError()
    {
        var sink = new CapturingSink();

        await RunAsync(ThrowingStream(null, TestContext.Current.CancellationToken), true, sink);

        var error = Assert.IsType<AgwErrorContent>(Assert.Single(sink.Messages[0].Contents));
        Assert.Equal("stream failed", error.Content);
        Assert.Equal(AgwTurnStatus.Failed, sink.Messages[^1].AdditionalProperties!["status"]);
    }

    /// <summary>
    /// 遍历全部消息类型常量：非流式立即转发与只输出结果的连接对控制消息的判定一致。
    /// Walks every message type constant: non-streaming immediate forwarding and result-only connections agree on control messages.
    /// </summary>
    [Fact]
    public void ControlClassification_EveryMessageType_MatchesResultOnlySink()
    {
        var types = typeof(AgwMessageTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();
        Assert.Contains(AgwMessageTypes.StepDiscarded, types);

        foreach (var type in types)
        {
            var message = CreateMessage("content", type);
            var forwarded = TurnPipeline.IsForwardedImmediately(message);
            var resultOnly = ResultOnlyMessageSink.ShouldWrite(message);
            Assert.Equal(AgwMessageClassifier.IsControl(message), forwarded);
            Assert.Equal(forwarded || type == AgwMessageTypes.Result, resultOnly);
        }
    }

    private static Task RunAsync(IAsyncEnumerable<AgwMessage> messages, bool stream, CapturingSink sink) =>
        TurnPipeline.RunAsync(
            Envelope,
            ExecutionTestScopes.Scope(),
            messages,
            stream,
            sink,
            TestContext.Current.CancellationToken
        );

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
