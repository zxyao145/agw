using Agw.Shared.Contracts.Agents;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public class AgentDomainServiceTests
{
    private readonly AgentDomainService _service = new();

    [Fact]
    public void PrepareForCreate_AssignsIdDefaultNameAndCreateMetadata()
    {
        var before = DateTime.UtcNow;
        var agent = new Agent
        {
            Type = AgentType.System,
            Name = "   ",
            ModelProviderId = Guid.NewGuid(),
        };

        _service.PrepareForCreate(agent, "tester");

        Assert.NotEqual(Guid.Empty, agent.Id);
        Assert.Equal(agent.Id.ToString(), agent.Name);
        Assert.Equal("tester", agent.CreateBy);
        Assert.InRange(agent.CreateTime, before, DateTime.UtcNow);
    }

    [Fact]
    public void PrepareForCreate_SystemAgentWithoutModelProvider_ThrowsAgwException()
    {
        var agent = new Agent
        {
            Type = AgentType.System,
            ModelProviderId = null,
        };

        var exception = Assert.Throws<AgwException>(() => _service.PrepareForCreate(agent, "tester"));
        Assert.Equal(ErrorCodes.SystemAgentRequiresModelProvider.Code, exception.Code);
    }

    [Fact]
    public void ApplyUpdate_SystemAgentWithoutModelProviderAfterUpdate_ThrowsAgwException()
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "system-agent",
            Type = AgentType.System,
            ModelProviderId = Guid.NewGuid(),
        };

        var exception = Assert.Throws<AgwException>(() =>
            _service.ApplyUpdate(agent, current => current.ModelProviderId = null, "tester"));

        Assert.Equal(ErrorCodes.SystemAgentRequiresModelProvider.Code, exception.Code);
    }

    [Fact]
    public void ApplyUpdate_ExternalAgent_PreservesImmutableFieldsWhileUpdatingMetadata()
    {
        var originalId = Guid.NewGuid();
        var originalCreateTime = DateTime.UtcNow.AddDays(-1);
        var agent = new Agent
        {
            Id = originalId,
            Name = "original-name",
            SystemPrompt = "original-prompt",
            Tools = "[\"tool-a\"]",
            Type = AgentType.External,
            DisplayName = "Before",
            CreateBy = "creator",
            CreateTime = originalCreateTime,
        };
        var updatedModelProviderId = Guid.NewGuid();

        _service.ApplyUpdate(
            agent,
            current =>
            {
                current.Id = Guid.NewGuid();
                current.Name = "updated-name";
                current.SystemPrompt = "updated-prompt";
                current.Tools = "[\"tool-b\"]";
                current.Type = AgentType.System;
                current.DisplayName = "After";
                current.ModelProviderId = updatedModelProviderId;
            },
            "updater");

        Assert.Equal(originalId, agent.Id);
        Assert.Equal("original-name", agent.Name);
        Assert.Equal("original-prompt", agent.SystemPrompt);
        Assert.Equal("[\"tool-a\"]", agent.Tools);
        Assert.Equal(AgentType.External, agent.Type);
        Assert.Equal("After", agent.DisplayName);
        Assert.Equal(updatedModelProviderId, agent.ModelProviderId);
        Assert.Equal("updater", agent.UpdateBy);
        Assert.True(agent.UpdateTime >= originalCreateTime);
    }

    [Fact]
    public void NormalizeMcpToolServerIds_RemovesEmptyValuesAndDuplicates()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var result = _service.NormalizeMcpToolServerIds([Guid.Empty, first, second, first]);

        Assert.Equal([first, second], result);
    }
}
