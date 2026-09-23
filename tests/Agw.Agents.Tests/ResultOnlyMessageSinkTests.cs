using Agw.Agents.Execution.Outbound;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public class ResultOnlyMessageSinkTests
{
    [Fact]
    public async Task WriteAsync_ResultMessage_ReachesInnerSink()
    {
        var inner = new CapturingSink();
        var sink = new ResultOnlyMessageSink(inner);

        await sink.WriteAsync(CreateMessage("summary", type: "result"), TestContext.Current.CancellationToken);

        Assert.Equal("summary", Assert.IsType<AgwTextContent>(Assert.Single(inner.Messages).Contents[0]).Content);
    }

    [Fact]
    public async Task WriteAsync_ResultMarkedOnContent_ReachesInnerSink()
    {
        var inner = new CapturingSink();
        var sink = new ResultOnlyMessageSink(inner);
        var message = CreateMessage("external result");
        message.Contents[0].AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = "result" };

        await sink.WriteAsync(message, TestContext.Current.CancellationToken);

        Assert.Single(inner.Messages);
    }

    [Theory]
    [InlineData("turn-start")]
    [InlineData("turn-finished")]
    [InlineData("interaction-request")]
    [InlineData("agentflow-checkpoint")]
    [InlineData("human-gate-request")]
    [InlineData("tool-approval-request")]
    public async Task WriteAsync_ControlMessage_ReachesInnerSink(string messageType)
    {
        var inner = new CapturingSink();
        var sink = new ResultOnlyMessageSink(inner);

        await sink.WriteAsync(CreateMessage("control", messageType), TestContext.Current.CancellationToken);

        Assert.Single(inner.Messages);
    }

    [Fact]
    public async Task WriteAsync_ErrorMessage_ReachesInnerSink()
    {
        var inner = new CapturingSink();
        var sink = new ResultOnlyMessageSink(inner);
        var message = new AgwMessage(
            Guid.CreateVersion7().ToString("D"),
            Agw.Shared.Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = "boom" }]
        );

        await sink.WriteAsync(message, TestContext.Current.CancellationToken);

        Assert.Single(inner.Messages);
    }

    [Fact]
    public async Task WriteAsync_ProcessMessage_IsDropped()
    {
        var inner = new CapturingSink();
        var sink = new ResultOnlyMessageSink(inner);

        await sink.WriteAsync(CreateMessage("assistant text"), TestContext.Current.CancellationToken);
        await sink.WriteAsync(CreateMessage("tool state", "tool-state"), TestContext.Current.CancellationToken);

        Assert.Empty(inner.Messages);
    }

    [Fact]
    public async Task Wrap_WithoutResultOnly_ForwardsEveryMessage()
    {
        var inner = new CapturingSink();

        var sink = ResultOnlyMessageSink.Wrap(inner, resultOnly: false);
        await sink.WriteAsync(CreateMessage("assistant text"), TestContext.Current.CancellationToken);

        Assert.Same(inner, sink);
        Assert.Single(inner.Messages);
    }

    private static AgwMessage CreateMessage(string content, string? type = null)
    {
        var properties = type == null ? null : new AdditionalPropertiesDictionary { ["type"] = type };
        return new AgwMessage(
            Guid.CreateVersion7().ToString("D"),
            Agw.Shared.Constants.DefaultAgentAuthor,
            AiRole.Assistant,
            [new AgwTextContent { Content = content }],
            properties
        );
    }

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
