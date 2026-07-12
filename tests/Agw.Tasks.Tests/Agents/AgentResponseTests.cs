using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Tests;

public class AgentResponseTests
{
    [Fact]
    public void FromDomain_WhenAgentHasRelations_MapsRelationIds()
    {
        var agentId = Guid.NewGuid();
        var mcpToolServerId = Guid.NewGuid();
        var skillId = Guid.NewGuid();
        var appInstanceId = Guid.NewGuid();

        var agent = new Agent
        {
            Id = agentId,
            DisplayName = "Writer",
            Name = "writer",
            Description = "Writes content",
            SystemPrompt = "Write clearly",
            ModelProviderId = Guid.NewGuid(),
            Tools = """["read_file"]""",
            Type = AgentType.System,
            Extra = """{"mode":"draft"}""",
            CreateBy = "tester",
            CreateTime = new DateTime(2026, 4, 11, 0, 0, 0, DateTimeKind.Utc),
            UpdateBy = "updater",
            UpdateTime = new DateTime(2026, 4, 11, 1, 0, 0, DateTimeKind.Utc),
            AgentMcpToolServers =
            [
                new AgentMcpServerRelation { AgentId = agentId, McpToolServerId = mcpToolServerId }
            ],
            AgentSkillRelations =
            [
                new AgentSkillRelation { AgentId = agentId, SkillId = skillId }
            ],
            AgentAppRelations =
            [
                new AgentAppRelation { AgentId = agentId, AppInstanceId = appInstanceId }
            ]
        };

        var response = AgentResponse.FromDomain(agent);

        Assert.Equal(agentId, response.Id);
        Assert.Equal("Writer", response.DisplayName);
        Assert.Equal("writer", response.Name);
        Assert.Equal("Writes content", response.Description);
        Assert.Equal("Write clearly", response.SystemPrompt);
        Assert.Equal(agent.ModelProviderId, response.ModelProviderId);
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

        var appRelation = Assert.Single(response.AgentAppRelations);
        Assert.Equal(agentId, appRelation.AgentId);
        Assert.Equal(appInstanceId, appRelation.AppInstanceId);
    }
}
