using Agw.Agents.Execution.Agentflows;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public partial class AgentflowRuntimeServiceTests
{
    [Fact]
    public async Task GetMermaidAsync_IndirectCycle_RejectsAndDisposesCreatedAgents()
    {
        var first = new Agentflow
        {
            Id = Guid.NewGuid(),
            CreateBy = "tester",
            Name = "first",
        };
        var second = new Agentflow
        {
            Id = Guid.NewGuid(),
            CreateBy = "tester",
            Name = "second",
        };
        var agentId = Guid.NewGuid();
        var nodes = new[]
        {
            new AgentflowNode
            {
                AgentflowId = first.Id,
                NodeId = "worker",
                Kind = AgentflowNodeKind.Agent,
                RelateId = agentId,
            },
            new AgentflowNode
            {
                AgentflowId = first.Id,
                NodeId = "nested",
                Kind = AgentflowNodeKind.WorkflowAsAgent,
                RelateId = second.Id,
            },
            new AgentflowNode
            {
                AgentflowId = first.Id,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
            },
            new AgentflowNode
            {
                AgentflowId = second.Id,
                NodeId = "nested",
                Kind = AgentflowNodeKind.WorkflowAsAgent,
                RelateId = first.Id,
            },
            new AgentflowNode
            {
                AgentflowId = second.Id,
                NodeId = "output",
                Kind = AgentflowNodeKind.Output,
            },
        };
        var edges = new[]
        {
            new AgentflowEdge
            {
                AgentflowId = first.Id,
                EdgeId = "worker-nested",
                SourceNodeId = "worker",
                TargetNodeId = "nested",
            },
            new AgentflowEdge
            {
                AgentflowId = first.Id,
                EdgeId = "nested-output",
                SourceNodeId = "nested",
                TargetNodeId = "output",
            },
            new AgentflowEdge
            {
                AgentflowId = second.Id,
                EdgeId = "nested-output",
                SourceNodeId = "nested",
                TargetNodeId = "output",
            },
        };
        var agents = new StubAgentRuntimeService(agentId);
        var service = CreateRuntimeService(
            NullLogger<AgentflowRuntimeService>.Instance,
            new TestRepository<Agentflow>([first, second], item => item.Id),
            new TestRepository<AgentflowNode>(nodes, item => (item.AgentflowId, item.NodeId)),
            new TestRepository<AgentflowEdge>(edges, item => (item.AgentflowId, item.EdgeId)),
            agents,
            new StubProviderSessionState(),
            new RecordingSummaryService()
        );

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            service.GetMermaidAsync(first.Id, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Contains(first.Id.ToString(), error.Message);
        Assert.NotEmpty(agents.CreatedAgents);
        Assert.All(agents.CreatedAgents, agent => Assert.True(agent.Disposed));
    }
}
