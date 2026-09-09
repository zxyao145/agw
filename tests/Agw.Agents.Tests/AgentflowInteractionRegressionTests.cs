using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Tests;

public partial class AgentflowRuntimeServiceTests
{
    [Fact]
    public async Task ExecuteStreamingAsync_ResponseDuringPublication_IsAlreadyRegistered()
    {
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => new ApprovalRequestAgent()
        );
        var sink = new InteractionTestSink();
        var interactions = new InProcessInteractionSession(sink);
        var accepted = false;
        sink.OnWrite = async (message, token) =>
            accepted = await interactions.TrySubmitAsync(
                InteractionTestData.Decision(InteractionTestData.Read(message), true),
                token
            );
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await CollectAsync(
                fixture.Service.ExecuteStreamingAsync(
                    fixture.Flow.Id,
                    "input",
                    cancellation.Token,
                    interactionHandler: interactions
                )
            );
            Assert.True(accepted);
            Assert.Single(sink.Messages);
        }
        finally
        {
            interactions.CancelAll();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteStreamingAsync_ParallelHumanGates_ResolvesOrCancelsEveryPending(int approvedCount)
    {
        var fixture = await CreateParallelGateFixtureAsync();
        var sink = new InteractionTestSink();
        var waiting = 0;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interactions = new InProcessInteractionSession(sink, pendingCountChanged: count => waiting = count);
        sink.OnWrite = (_, _) =>
        {
            if (sink.Messages.Count == 2)
                published.TrySetResult();
            return ValueTask.CompletedTask;
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var run = CollectAsync(
            fixture.Service.ExecuteStreamingAsync(
                fixture.Flow.Id,
                "input",
                cancellation.Token,
                interactionHandler: interactions
            )
        );
        await published.Task.WaitAsync(cancellation.Token);
        Assert.Equal(2, waiting);
        var requests = sink.Messages.Select(InteractionTestData.Read).ToArray();
        Assert.True(
            await interactions.TrySubmitAsync(
                InteractionTestData.Decision(requests[0], approvedCount > 0),
                cancellation.Token
            )
        );
        if (approvedCount > 0)
        {
            Assert.Equal(1, waiting);
            Assert.True(
                await interactions.TrySubmitAsync(
                    InteractionTestData.Decision(requests[1], approvedCount == 2),
                    cancellation.Token
                )
            );
        }
        var messages = await run;
        Assert.Equal(0, waiting);
        Assert.Equal(
            approvedCount == 2 ? 0 : 1,
            messages.Count(message => MessageShape(message) == "human-gate-rejected")
        );
        Assert.False(
            await interactions.TrySubmitAsync(
                InteractionTestData.Decision(requests[1], true),
                TestContext.Current.CancellationToken
            )
        );
    }

    private static async Task<CharacterizationFixture> CreateParallelGateFixtureAsync()
    {
        var fixture = CreateCharacterizationFixture([
            AgentflowNodeKind.HumanGate,
            AgentflowNodeKind.HumanGate,
            AgentflowNodeKind.Agent,
            AgentflowNodeKind.Output,
        ]);
        fixture.Edges.Remove(fixture.Edges.Queryable.Single(edge => edge.EdgeId == "edge-0"));
        await fixture.Edges.AddAsync(
            new AgentflowEdge
            {
                AgentflowId = fixture.Flow.Id,
                EdgeId = "input-second",
                SourceNodeId = "input",
                TargetNodeId = "node-1",
            }
        );
        await fixture.Edges.AddAsync(
            new AgentflowEdge
            {
                AgentflowId = fixture.Flow.Id,
                EdgeId = "parallel",
                SourceNodeId = "node-0",
                TargetNodeId = "node-2",
            }
        );
        return fixture;
    }

    [Theory]
    [InlineData(AgwPermissionMode.AlwaysAsk, "always-tool", "once")]
    [InlineData(AgwPermissionMode.AllowSameArguments, "once", "always-arguments")]
    public async Task ExecuteDurableSegmentAsync_ManualScope_EnforcesPermissionMode(
        AgwPermissionMode mode,
        string submittedScope,
        string expectedScope
    )
    {
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => new ApprovalRequestAgent()
        );
        var manifest = CreateManifest(fixture.Flow.Id);
        manifest = manifest with { Settings = manifest.Settings with { PermissionMode = mode } };
        var sink = new RecordingSegmentSink();
        var token = TestContext.Current.CancellationToken;
        var waiting = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 0, [], null),
            sink,
            token
        );
        var request = Assert.Single(waiting.PendingInteractions);
        var response = CreateResponse(manifest, request, true, submittedScope);

        var result = await fixture.Service.ExecuteDurableSegmentAsync(
            manifest,
            new(manifest.ExecutionId, 1, [response], waiting.Checkpoint),
            sink,
            token
        );

        Assert.Equal(DurableExecutionSegmentStatus.Completed, result.Status);
        Assert.Contains(sink.Messages, message => MessageShape(message) == expectedScope);
        Assert.DoesNotContain(sink.Messages, message => MessageShape(message) == submittedScope);
    }
}
