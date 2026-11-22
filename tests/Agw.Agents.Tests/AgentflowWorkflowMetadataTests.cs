using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public partial class AgentflowTurnExecutorTests
{
    [Fact]
    public void WorkflowCompilation_NodeIdsByExecutorId_MapsEachLogicalNode()
    {
        var flow = new Agentflow { Id = Guid.CreateVersion7(), Name = "metadata-flow" };
        var nodes = new[]
        {
            new AgentflowNode { NodeId = "input", Kind = AgentflowNodeKind.Input },
            new AgentflowNode { NodeId = "output", Kind = AgentflowNodeKind.Output },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                EdgeId = "input-output",
                SourceNodeId = "input",
                TargetNodeId = "output",
            },
        };
        var compiler = new AgentflowWorkflowCompiler(NullLogger<AgentflowWorkflowCompiler>.Instance);

        var compilation = compiler.Compile(flow, 1, nodes, edges, [], new Dictionary<string, AIAgent>());

        Assert.NotNull(compilation);
        Assert.Equal(2, compilation.NodeIdsByExecutorId.Count);
        Assert.Equal(2, compilation.NodeIdsByExecutorId.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("input", compilation.NodeIdsByExecutorId.Values);
        Assert.Contains("output", compilation.NodeIdsByExecutorId.Values);
    }

    [Theory]
    [InlineData("streaming")]
    [InlineData("unattended")]
    [InlineData("durable")]
    [InlineData("compile")]
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
                var result = await fixture.Service.ExecuteDurableSegmentInScopeAsync(
                    manifest,
                    new(manifest.TurnId, 0, [], null),
                    new RecordingSegmentSink(),
                    TestContext.Current.CancellationToken
                );
                Assert.Equal(DurableExecutionSegmentStatus.Completed, result.Status);
                break;
            default:
                await using (
                    var workflow = await fixture.Service.CreateAiWorkflow(
                        fixture.Flow.Id,
                        AgentflowTurnTestHost.FirstRevision,
                        TestContext.Current.CancellationToken
                    )
                )
                {
                    Assert.NotNull(workflow);
                }
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
        fixture.Nodes[1].ConfigJson = GateOptions("original prompt");
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
            fixture.Nodes[1].ConfigJson = GateOptions("updated prompt");
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
