using System.Runtime.CompilerServices;

using Agw.Agents.Runtime.Execution;
using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Tests;

public class ExecutionTurnRunnerTests
{
    [Fact]
    public async Task RunAsync_Streaming_EmitsStartContentAndCompletedFinish()
    {
        var sink = new CapturingSink();
        var content = CreateMessage("content");

        await ExecutionTurnRunner.RunAsync(
            ToStream(content, cancellationToken: TestContext.Current.CancellationToken),
            true,
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(["turn-start", null, "turn-finished"], sink.Messages.Select(GetType));
        Assert.Equal("completed", sink.Messages[^1].AdditionalProperties!["status"]);
    }

    [Fact]
    public async Task RunAsync_NonStreaming_ForwardsHumanGateBeforeBufferedContent()
    {
        var sink = new CapturingSink();
        var content = CreateMessage("content");
        var gate = CreateMessage("gate", "human-gate-request");

        await ExecutionTurnRunner.RunAsync(
            ToStream(content, gate, TestContext.Current.CancellationToken),
            false,
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["turn-start", "human-gate-request", null, "turn-finished"],
            sink.Messages.Select(GetType));
    }

    private static async IAsyncEnumerable<AgwMessage> ToStream(
        AgwMessage first,
        AgwMessage? second = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return first;
        if (second != null) yield return second;
        await Task.CompletedTask;
    }

    private static AgwMessage CreateMessage(string content, string? type = null)
    {
        var properties = type == null
            ? null
            : new Microsoft.Extensions.AI.AdditionalPropertiesDictionary { ["type"] = type };
        return new AgwMessage(
            Guid.NewGuid().ToString("D"),
            Agw.Shared.Constants.DefaultAgentAuthor,
            AiRole.Assistant,
            [new AgwTextContent { Content = content }],
            properties);
    }

    private static string? GetType(AgwMessage message) =>
        message.AdditionalProperties?.TryGetValue("type", out var value) == true
            ? value as string
            : null;

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
