using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Testing;

namespace Agw.Agents.Tests;

public class AgentDomainServiceTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly AgentDomainService _service = new(new TestTimeProvider(UtcNow));

    [Fact]
    public void PrepareForCreate_AssignsIdDefaultNameAndCreateMetadata()
    {
        var agent = new Agent
        {
            Type = AgentType.System,
            Name = "   ",
            ModelProviderId = Guid.NewGuid(),
            EnvironmentVariables = null!,
        };

        _service.PrepareForCreate(agent, "tester");

        Assert.NotEqual(Guid.Empty, agent.Id);
        Assert.Equal(agent.Id.ToString(), agent.Name);
        Assert.Equal("tester", agent.CreateBy);
        Assert.Equal(UtcNow, agent.CreateTime);
        Assert.Empty(agent.EnvironmentVariables);
    }

    [Fact]
    public void PrepareForCreate_WithEnvironmentVariables_NormalizesNamesAndPreservesEmptyValues()
    {
        var agent = new Agent
        {
            Type = AgentType.System,
            ModelProviderId = Guid.NewGuid(),
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["  AGW_TOKEN  "] = "secret",
                ["EMPTY_VALUE"] = "",
            },
        };

        _service.PrepareForCreate(agent, "tester");

        Assert.Equal("secret", agent.EnvironmentVariables["AGW_TOKEN"]);
        Assert.Equal("", agent.EnvironmentVariables["EMPTY_VALUE"]);
    }

    [Fact]
    public void PrepareForCreate_WithDuplicateEnvironmentVariableNamesAfterTrim_ThrowsAgwException()
    {
        var agent = new Agent
        {
            Type = AgentType.System,
            ModelProviderId = Guid.NewGuid(),
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["AGW_TOKEN"] = "first",
                [" AGW_TOKEN "] = "second",
            },
        };

        var exception = Assert.Throws<AgwException>(() => _service.PrepareForCreate(agent, "tester"));

        Assert.Equal(ErrorCodes.InvalidAgentEnvironmentVariableName.Code, exception.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("INVALID=NAME")]
    [InlineData("INVALID\0NAME")]
    public void PrepareForCreate_WithInvalidEnvironmentVariableName_ThrowsAgwException(string name)
    {
        var agent = new Agent
        {
            Type = AgentType.System,
            ModelProviderId = Guid.NewGuid(),
            EnvironmentVariables = new Dictionary<string, string> { [name] = "value" },
        };

        var exception = Assert.Throws<AgwException>(() => _service.PrepareForCreate(agent, "tester"));

        Assert.Equal(ErrorCodes.InvalidAgentEnvironmentVariableName.Code, exception.Code);
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
    public void PrepareForCreate_ExternalAgentWithSummaryEnabled_DisablesSummary()
    {
        var agent = new Agent
        {
            Type = AgentType.External,
            EnableSummary = true,
        };

        _service.PrepareForCreate(agent, "tester");

        Assert.False(agent.EnableSummary);
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
        var originalCreateTime = UtcNow.AddDays(-1);
        var agent = new Agent
        {
            Id = originalId,
            Name = "original-name",
            SystemPrompt = "original-prompt",
            Tools = "[\"tool-a\"]",
            EnableSummary = false,
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
                current.EnableSummary = true;
                current.Type = AgentType.System;
                current.DisplayName = "After";
                current.ModelProviderId = updatedModelProviderId;
            },
            "updater");

        Assert.Equal(originalId, agent.Id);
        Assert.Equal("original-name", agent.Name);
        Assert.Equal("original-prompt", agent.SystemPrompt);
        Assert.Equal("[\"tool-a\"]", agent.Tools);
        Assert.False(agent.EnableSummary);
        Assert.Equal(AgentType.External, agent.Type);
        Assert.Equal("After", agent.DisplayName);
        Assert.Equal(updatedModelProviderId, agent.ModelProviderId);
        Assert.Equal("updater", agent.UpdateBy);
        Assert.Equal(UtcNow, agent.UpdateTime);
    }

    [Fact]
    public void ApplyUpdate_ExternalAgentWithValidExtra_UpdatesNormalizedExtra()
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "external-agent",
            Type = AgentType.External,
            Extra = "{\"before\":true}",
        };

        _service.ApplyUpdate(
            agent,
            current => current.Extra = "  {\"sandbox\":false}  ",
            "updater");

        Assert.Equal("{\"sandbox\":false}", agent.Extra);
    }

    [Fact]
    public void ApplyUpdate_ExternalAgentWithBlankExtra_ClearsExtra()
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "external-agent",
            Type = AgentType.External,
            Extra = "{\"before\":true}",
        };

        _service.ApplyUpdate(agent, current => current.Extra = "   ", "updater");

        Assert.Null(agent.Extra);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void ApplyUpdate_ExternalAgentWithInvalidExtra_ThrowsAgwException(string extra)
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "external-agent",
            Type = AgentType.External,
        };

        var exception = Assert.Throws<AgwException>(() =>
            _service.ApplyUpdate(agent, current => current.Extra = extra, "updater"));

        Assert.Equal(ErrorCodes.InvalidAgentExtraSettings.Code, exception.Code);
    }

    [Fact]
    public void ApplyUpdate_SystemAgent_PreservesExtra()
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "system-agent",
            Type = AgentType.System,
            ModelProviderId = Guid.NewGuid(),
            Extra = "{\"managed\":true}",
        };

        _service.ApplyUpdate(
            agent,
            current => current.Extra = "{\"managed\":false}",
            "updater");

        Assert.Equal("{\"managed\":true}", agent.Extra);
    }

    [Fact]
    public void ApplyUpdate_SystemAgent_UpdatesEnvironmentVariables()
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "system-agent",
            Type = AgentType.System,
            ModelProviderId = Guid.NewGuid(),
            EnvironmentVariables = new Dictionary<string, string> { ["BEFORE"] = "value" },
        };

        _service.ApplyUpdate(
            agent,
            current => current.EnvironmentVariables = new Dictionary<string, string>
            {
                ["AFTER"] = "",
            },
            "updater");

        Assert.Single(agent.EnvironmentVariables);
        Assert.Equal("", agent.EnvironmentVariables["AFTER"]);
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
