using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public partial class AgentflowRuntimeServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Execute_NodeHandoffs_PublishesDistinctInputsBeforeDownstreamOutput(bool durable, bool fanOut)
    {
        // Arrange: all three nodes deliberately reuse the same Agent definition and response ID.
        var agents = new List<ScriptedAgent>();
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Agent, AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ =>
            {
                var agent = new ScriptedAgent(["result"]);
                agents.Add(agent);
                return agent;
            }
        );
        if (fanOut)
        {
            fixture.Edges.Queryable.Single(edge => edge.EdgeId == "edge-1").SourceNodeId = "node-0";
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        // Act
        List<AgwMessage> messages;
        if (durable)
        {
            var manifest = CreateManifest(fixture.Flow.Id);
            var sink = new RecordingSegmentSink();
            var result = await fixture.Service.ExecuteDurableSegmentAsync(
                manifest,
                new(manifest.ExecutionId, 0, [], null),
                sink,
                timeout.Token
            );
            Assert.Equal(DurableExecutionSegmentStatus.Completed, result.Status);
            messages = sink.Messages;
        }
        else
        {
            messages = await CollectAsync(
                fixture.Service.ExecuteStreamingAsync(fixture.Flow.Id, "input", timeout.Token)
            );
        }

        // Assert: these are the exact messages delivered to the history-owning inner Agent.
        var inputs = messages.Where(IsLiveNodeInput).ToList();
        Assert.Equal(2, inputs.Count);
        Assert.Equal(2, inputs.Select(message => message.MessageId).Distinct().Count());
        Assert.All(
            inputs,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("scripted", message.Author);
                Assert.Equal("result", MessageShape(message));
                Assert.NotEqual("scripted-message", message.MessageId);
            }
        );
        Assert.Single(messages, message => message.Role == "user" && !IsLiveNodeInput(message));
        var receivedInputs = agents
            .SelectMany(agent => agent.Inputs)
            .SelectMany(input => input)
            .Where(message => message.Role == ChatRole.User && message.Text == "result")
            .ToList();
        Assert.Equal(
            inputs.Select(message => message.MessageId).Order(),
            receivedInputs.Select(message => message.MessageId).Order()
        );
        foreach (var input in inputs)
        {
            var source = input.AdditionalProperties!["nodeName"]?.ToString();
            var sourceOutput = messages.FindIndex(message =>
                message.Role == "assistant" && message.AdditionalProperties?["nodeName"]?.ToString() == source
            );
            Assert.InRange(sourceOutput, 0, messages.IndexOf(input) - 1);
        }
        foreach (var agent in agents.Skip(1))
        {
            var received = agent.Inputs.SelectMany(batch => batch).Single(message => message.Text == "result");
            var inputIndex = messages.FindIndex(message => message.MessageId == received.MessageId);
            Assert.Contains(messages.Skip(inputIndex + 1), message => message.Role == "assistant");
        }
    }

    [Fact]
    public async Task ExecuteStreamingAsync_DownstreamFails_KeepsInputWithoutRoutingItAsOutput()
    {
        // Arrange
        var count = 0;
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Agent, AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => new ScriptedAgent(["result"], fail: count++ == 1)
        );
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        // Act
        var messages = await CollectAsync(
            fixture.Service.ExecuteStreamingAsync(fixture.Flow.Id, "input", timeout.Token)
        );

        // Assert
        Assert.Single(messages, IsLiveNodeInput);
        Assert.Contains(messages, message => MessageShape(message) == "workflow-error");
        Assert.DoesNotContain(
            messages,
            message => message.AdditionalProperties?.GetValueOrDefault("nodeName")?.ToString() == "Node 2"
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteStreamingAsync_DownstreamWaits_InputIsLiveAndCancellationReleasesTheStream(bool cancel)
    {
        // Arrange
        var waiting = new WaitingForInputAgent();
        var count = 0;
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => count++ == 0 ? new ScriptedAgent(["review"]) : waiting
        );
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var messages = new List<AgwMessage>();

        // Act: no second-node response can exist until the client sees and releases its input.
        await foreach (var message in fixture.Service.ExecuteStreamingAsync(fixture.Flow.Id, "input", timeout.Token))
        {
            messages.Add(message);
            if (IsLiveNodeInput(message))
            {
                await waiting.Started.Task.WaitAsync(timeout.Token);
                if (cancel)
                    timeout.Cancel();
                else
                    waiting.Release.TrySetResult();
            }
        }

        // Assert
        Assert.Single(messages, IsLiveNodeInput);
        Assert.Equal(!cancel, messages.Any(message => MessageShape(message) == "downstream"));
    }

    private sealed class WaitingForInputAgent : DelegatingAIAgent
    {
        public WaitingForInputAgent()
            : base(new ScriptedAgent(["downstream"])) { }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await foreach (var update in InnerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
                yield return update;
        }
    }

    private static bool IsLiveNodeInput(AgwMessage message) =>
        message.AdditionalProperties?.TryGetValue(ConversationHistoryMetadata.AgentflowInputKey, out var value) == true
        && value?.ToString() == bool.TrueString;
}
