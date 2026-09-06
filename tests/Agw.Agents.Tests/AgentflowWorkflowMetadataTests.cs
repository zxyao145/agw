using Agw.Agents.Execution.Durable;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Tests;

public partial class AgentflowRuntimeServiceTests
{
    [Theory]
    [InlineData("streaming")]
    [InlineData("unattended")]
    [InlineData("durable")]
    [InlineData("mermaid")]
    public async Task WorkflowConstruction_EachEntryPoint_LoadsNodesOnce(string entryPoint)
    {
        var fixture = CreateCharacterizationFixture([
            AgentflowNodeKind.Agent,
            AgentflowNodeKind.CheckpointMarker,
            AgentflowNodeKind.Output,
        ]);

        switch (entryPoint)
        {
            case "streaming":
                await CollectAsync(
                    fixture.Service.ExecuteStreamingAsync(
                        fixture.Flow.Id,
                        "input",
                        TestContext.Current.CancellationToken
                    )
                );
                break;
            case "unattended":
                Assert.NotNull(
                    await fixture.Service.ExecuteAsync(
                        fixture.Flow.Id,
                        Guid.CreateVersion7(),
                        "input",
                        TestContext.Current.CancellationToken
                    )
                );
                break;
            case "durable":
                var manifest = CreateManifest(fixture.Flow.Id);
                var result = await fixture.Service.ExecuteDurableSegmentAsync(
                    manifest,
                    new(manifest.ExecutionId, 0, [], null),
                    new RecordingSegmentSink(),
                    TestContext.Current.CancellationToken
                );
                Assert.Equal(DurableExecutionSegmentStatus.Completed, result.Status);
                break;
            default:
                Assert.NotNull(
                    await fixture.Service.GetMermaidAsync(fixture.Flow.Id, TestContext.Current.CancellationToken)
                );
                break;
        }

        Assert.Equal(1, fixture.NodeRepository.ListCallCount);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_NodeChangesAfterBuild_UsesBuiltMetadataThenRefreshesNextRun()
    {
        var fixture = CreateCharacterizationFixture([
            AgentflowNodeKind.Agent,
            AgentflowNodeKind.HumanGate,
            AgentflowNodeKind.Output,
        ]);
        fixture.Nodes[1].ConfigJson = """{"humanPrompt":"original prompt"}""";
        var first = new List<AgwMessage>();

        await foreach (
            var message in fixture.Service.ExecuteStreamingAsync(
                fixture.Flow.Id,
                "input",
                TestContext.Current.CancellationToken,
                humanGateApprovalHandler: new FixedApprovalHandler(false)
            )
        )
        {
            first.Add(message);
            fixture.Nodes[1].ConfigJson = """{"humanPrompt":"updated prompt"}""";
            fixture.Nodes[1].Name = "updated name";
        }
        var second = await CollectAsync(
            fixture.Service.ExecuteStreamingAsync(
                fixture.Flow.Id,
                "input",
                TestContext.Current.CancellationToken,
                humanGateApprovalHandler: new FixedApprovalHandler(false)
            )
        );

        var original = Assert.Single(first, message => MessageShape(message) == "human-gate-request");
        var updated = Assert.Single(second, message => MessageShape(message) == "human-gate-request");
        Assert.Equal("original prompt", original.AdditionalProperties!["prompt"]);
        Assert.Equal("Node 1", original.AdditionalProperties["nodeName"]);
        Assert.Equal("updated prompt", updated.AdditionalProperties!["prompt"]);
        Assert.Equal("updated name", updated.AdditionalProperties["nodeName"]);
        Assert.Equal(2, fixture.NodeRepository.ListCallCount);
    }
}
