using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Tests;

public partial class AgentflowTurnExecutorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteDurableSegmentAsync_ResumeValidation_UnmatchedAnswerBeforeNewWaitFails(bool toolAnswer)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateCharacterizationFixture([
            AgentflowNodeKind.HumanGate,
            AgentflowNodeKind.HumanGate,
            AgentflowNodeKind.Agent,
            AgentflowNodeKind.Output,
        ]);
        var manifest = CreateManifest(fixture.Flow.Id);
        var sink = new RecordingSegmentSink();
        var waiting = await fixture.Service.ExecuteDurableSegmentInScopeAsync(
            manifest,
            new(manifest.ExecutionId, 0, [], null),
            sink,
            token
        );
        Assert.Equal(DurableExecutionSegmentStatus.WaitingForHuman, waiting.Status);
        Assert.NotNull(waiting.Checkpoint);
        var first = Assert.Single(waiting.PendingInteractions);
        InteractionRequest unmatched = toolAnswer
            ? InteractionTestData.Tool("missing-tool-answer")
            : InteractionTestData.Gate("missing-workflow-answer");

        // Inject a persisted answer whose original request is absent from the restored checkpoint.
        var resumed = await fixture.Service.ExecuteDurableSegmentInScopeAsync(
            manifest,
            new(
                manifest.ExecutionId,
                1,
                [CreateResponse(manifest, first, true), CreateResponse(manifest, unmatched, true)],
                waiting.Checkpoint
            ),
            sink,
            token
        );

        Assert.Equal(DurableExecutionSegmentStatus.Failed, resumed.Status);
        Assert.Equal($"Agentflow did not restore human request '{unmatched.InteractionId}'.", resumed.ErrorMessage);
        Assert.Empty(resumed.PendingInteractions);
        Assert.Null(resumed.Checkpoint);
    }

    [Fact]
    public async Task ExecuteDurableSegmentAsync_ResumeValidation_ConsumedAnswerAllowsNewWait()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateCharacterizationFixture([
            AgentflowNodeKind.HumanGate,
            AgentflowNodeKind.HumanGate,
            AgentflowNodeKind.Agent,
            AgentflowNodeKind.Output,
        ]);
        var manifest = CreateManifest(fixture.Flow.Id);
        var sink = new RecordingSegmentSink();
        var waiting = await fixture.Service.ExecuteDurableSegmentInScopeAsync(
            manifest,
            new(manifest.ExecutionId, 0, [], null),
            sink,
            token
        );
        var first = Assert.Single(waiting.PendingInteractions);

        var resumed = await fixture.Service.ExecuteDurableSegmentInScopeAsync(
            manifest,
            new(manifest.ExecutionId, 1, [CreateResponse(manifest, first, true)], waiting.Checkpoint),
            sink,
            token
        );

        Assert.Equal(DurableExecutionSegmentStatus.WaitingForHuman, resumed.Status);
        var second = Assert.Single(resumed.PendingInteractions);
        Assert.Equal("node-1", second.Source.NodeId);
        Assert.NotEqual(first.InteractionId, second.InteractionId);
        Assert.NotNull(resumed.Checkpoint);
        Assert.Null(resumed.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteDurableSegmentAsync_ResumeValidation_RejectedGateStillStopsImmediately()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateCharacterizationFixture([
            AgentflowNodeKind.HumanGate,
            AgentflowNodeKind.HumanGate,
            AgentflowNodeKind.Agent,
            AgentflowNodeKind.Output,
        ]);
        var manifest = CreateManifest(fixture.Flow.Id);
        var sink = new RecordingSegmentSink();
        var waiting = await fixture.Service.ExecuteDurableSegmentInScopeAsync(
            manifest,
            new(manifest.ExecutionId, 0, [], null),
            sink,
            token
        );
        var first = Assert.Single(waiting.PendingInteractions);

        var resumed = await fixture.Service.ExecuteDurableSegmentInScopeAsync(
            manifest,
            new(
                manifest.ExecutionId,
                1,
                [
                    CreateResponse(manifest, first, false),
                    CreateResponse(manifest, InteractionTestData.Tool("unneeded-answer"), true),
                ],
                waiting.Checkpoint
            ),
            sink,
            token
        );

        Assert.Equal(DurableExecutionSegmentStatus.Completed, resumed.Status);
        Assert.Null(resumed.ErrorMessage);
        Assert.Empty(resumed.PendingInteractions);
        Assert.Single(sink.Messages, message => MessageShape(message) == "human-gate-rejected");
    }
}
