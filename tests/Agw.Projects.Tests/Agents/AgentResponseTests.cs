using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Tooling;

namespace Agw.Agents.Tests;

public class AgentResponseTests
{
    [Fact]
    public void FromDomain_WhenAgentHasRelations_MapsRelationIds()
    {
        var agentId = Guid.CreateVersion7();
        var mcpToolServerId = Guid.CreateVersion7();
        var skillId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();

        var agent = new Agent
        {
            Id = agentId,
            DisplayName = "Writer",
            Name = "writer",
            Description = "Writes content",
            SystemPrompt = "Write clearly",
            ModelProviderId = Guid.CreateVersion7(),
            EnableSummary = true,
            Tools = [new ToolValue { Definition = new WebFetchToolDefinition() }],
            Type = AgentType.System,
            Extra = """{"mode":"draft"}""",
            CreateBy = "tester",
            CreateTime = new DateTimeOffset(2026, 4, 11, 0, 0, 0, TimeSpan.Zero),
            UpdateBy = "updater",
            UpdateTime = new DateTimeOffset(2026, 4, 11, 1, 0, 0, TimeSpan.Zero),
            AgentMcpToolServers = [new AgentMcpServerRelation { AgentId = agentId, McpToolServerId = mcpToolServerId }],
            AgentSkillRelations = [new AgentSkillRelation { AgentId = agentId, SkillId = skillId }],
            AgentConnectionRelations = [new AgentConnectionRelation { AgentId = agentId, ConnectionId = connectionId }],
        };

        var response = AgentResponse.FromDomain(agent);

        Assert.Equal(agentId, response.Id);
        Assert.Equal("Writer", response.DisplayName);
        Assert.Equal("writer", response.Name);
        Assert.Equal("Writes content", response.Description);
        Assert.Equal("Write clearly", response.SystemPrompt);
        Assert.Equal(agent.ModelProviderId, response.ModelProviderId);
        Assert.True(response.EnableSummary);
        Assert.Equal(agent.Tools, response.Tools);
        Assert.Equal(AgentType.System, response.Type);
        Assert.Equal(agent.Extra, response.Extra);
        Assert.Equal(agent.CreateBy, response.CreateBy);
        Assert.Equal(agent.CreateTime, response.CreateTime);
        Assert.Equal(agent.UpdateBy, response.UpdateBy);
        Assert.Equal(agent.UpdateTime, response.UpdateTime);

        var mcpRelation = Assert.Single(response.AgentMcpToolServers);
        Assert.Equal(agentId, mcpRelation.AgentId);
        Assert.Equal(mcpToolServerId, mcpRelation.McpToolServerId);

        var skillRelation = Assert.Single(response.AgentSkillRelations);
        Assert.Equal(agentId, skillRelation.AgentId);
        Assert.Equal(skillId, skillRelation.SkillId);

        var connectionRelation = Assert.Single(response.AgentConnectionRelations);
        Assert.Equal(agentId, connectionRelation.AgentId);
        Assert.Equal(connectionId, connectionRelation.ConnectionId);
    }
}
