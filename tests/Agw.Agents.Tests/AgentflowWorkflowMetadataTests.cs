using Agw.Agents.Execution.Runtimes.Durable.Contracts;
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
        var firstHandler = new FixedApprovalHandler(false);
        var secondHandler = new FixedApprovalHandler(false);

        await foreach (
            var message in fixture.Service.ExecuteStreamingAsync(
                fixture.Flow.Id,
                "input",
                TestContext.Current.CancellationToken,
                interactionHandler: firstHandler
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
                interactionHandler: secondHandler
            )
        );

        var original = Assert.Single(firstHandler.Requests);
        var updated = Assert.Single(secondHandler.Requests);
        Assert.Equal("original prompt", original.Prompt);
        Assert.Equal("Node 1", original.Source.NodeName);
        Assert.Equal("updated prompt", updated.Prompt);
        Assert.Equal("updated name", updated.Source.NodeName);
        Assert.Equal(2, fixture.NodeRepository.ListCallCount);
    }
}
