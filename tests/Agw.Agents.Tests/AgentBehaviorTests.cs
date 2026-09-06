using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Tooling;

namespace Agw.Agents.Tests;

public class AgentBehaviorTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PrepareForCreate_AssignsIdAndDefaultNameWithoutAuditStamping()
    {
        var agent = new Agent
        {
            Type = AgentType.System,
            Name = "   ",
            ModelProviderId = Guid.CreateVersion7(),
            EnvironmentVariables = null!,
        };

        new AgentBehavior(agent).PrepareForCreate();

        Assert.NotEqual(Guid.Empty, agent.Id);
        Assert.Equal(agent.Id.ToString(), agent.Name);
        Assert.Null(agent.CreateBy);
        Assert.Equal(default, agent.CreateTime);
        Assert.Empty(agent.EnvironmentVariables);
    }

    [Fact]
    public void PrepareForCreate_WithEnvironmentVariables_NormalizesNamesAndPreservesEmptyValues()
    {
        var agent = new Agent
        {
            Type = AgentType.System,
            ModelProviderId = Guid.CreateVersion7(),
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["  AGW_TOKEN  "] = "secret",
                ["EMPTY_VALUE"] = "",
            },
        };

        new AgentBehavior(agent).PrepareForCreate();

        Assert.Equal("secret", agent.EnvironmentVariables["AGW_TOKEN"]);
        Assert.Equal("", agent.EnvironmentVariables["EMPTY_VALUE"]);
    }

    [Fact]
    public void PrepareForCreate_WithDuplicateEnvironmentVariableNamesAfterTrim_ThrowsAgwException()
    {
        var agent = new Agent
        {
            Type = AgentType.System,
            ModelProviderId = Guid.CreateVersion7(),
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["AGW_TOKEN"] = "first",
                [" AGW_TOKEN "] = "second",
            },
        };

        var exception = Assert.Throws<AgwException>(() => new AgentBehavior(agent).PrepareForCreate());

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
            ModelProviderId = Guid.CreateVersion7(),
            EnvironmentVariables = new Dictionary<string, string> { [name] = "value" },
        };

        var exception = Assert.Throws<AgwException>(() => new AgentBehavior(agent).PrepareForCreate());

        Assert.Equal(ErrorCodes.InvalidAgentEnvironmentVariableName.Code, exception.Code);
    }

    [Fact]
    public void PrepareForCreate_SystemAgentWithoutModelProvider_ThrowsAgwException()
    {
        var agent = new Agent { Type = AgentType.System, ModelProviderId = null };

        var exception = Assert.Throws<AgwException>(() => new AgentBehavior(agent).PrepareForCreate());
        Assert.Equal(ErrorCodes.SystemAgentRequiresModelProvider.Code, exception.Code);
    }

    [Fact]
    public void PrepareForCreate_ExternalAgentWithSummaryEnabledAndSummaryModelProvider_PreservesSummary()
    {
        var summaryModelProviderId = Guid.CreateVersion7();
        var agent = new Agent
        {
            Type = AgentType.External,
            EnableSummary = true,
            ModelProviderId = null,
            SummaryModelProviderId = summaryModelProviderId,
        };

        new AgentBehavior(agent).PrepareForCreate();

        Assert.True(agent.EnableSummary);
        Assert.Equal(summaryModelProviderId, agent.SummaryModelProviderId);
    }

    [Fact]
    public void PrepareForCreate_ExternalAgentWithSummaryEnabledWithoutModelProvider_ThrowsAgwException()
    {
        var agent = new Agent
        {
            Type = AgentType.External,
            EnableSummary = true,
            ModelProviderId = null,
            SummaryModelProviderId = null,
        };

        var exception = Assert.Throws<AgwException>(() => new AgentBehavior(agent).PrepareForCreate());

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public void PrepareForCreate_SystemAgentWithSummaryEnabled_DefaultsToAgentModelProvider()
    {
        var agent = new Agent
        {
            Type = AgentType.System,
            EnableSummary = true,
            ModelProviderId = Guid.CreateVersion7(),
            SummaryModelProviderId = null,
        };

        new AgentBehavior(agent).PrepareForCreate();

        Assert.True(agent.EnableSummary);
        Assert.Null(agent.SummaryModelProviderId);
    }

    [Fact]
    public void ApplyUpdate_SystemAgentWithoutModelProviderAfterUpdate_ThrowsAgwException()
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            Type = AgentType.System,
            ModelProviderId = Guid.CreateVersion7(),
        };

        var exception = Assert.Throws<AgwException>(() =>
            new AgentBehavior(agent).ApplyUpdate(current => current.ModelProviderId = null)
        );

        Assert.Equal(ErrorCodes.SystemAgentRequiresModelProvider.Code, exception.Code);
    }

    [Fact]
    public void ApplyUpdate_ExternalAgent_PreservesImmutableFieldsAndAuditWhileUpdatingConfiguration()
    {
        var originalId = Guid.CreateVersion7();
        var originalCreateTime = UtcNow.AddDays(-1);
        var agent = new Agent
        {
            Id = originalId,
            Name = "original-name",
            SystemPrompt = "original-prompt",
            Tools = [new ToolValue { Definition = new WebSearchToolDefinition() }],
            EnableSummary = false,
            Type = AgentType.External,
            DisplayName = "Before",
            CreateBy = "creator",
            CreateTime = originalCreateTime,
        };
        var originalTools = agent.Tools;
        var updatedModelProviderId = Guid.CreateVersion7();
        var updatedSummaryModelProviderId = Guid.CreateVersion7();

        new AgentBehavior(agent).ApplyUpdate(current =>
        {
            current.Id = Guid.CreateVersion7();
            current.Name = "updated-name";
            current.SystemPrompt = "updated-prompt";
            current.Tools = [new ToolValue { Definition = new WebFetchToolDefinition() }];
            current.EnableSummary = true;
            current.Type = AgentType.System;
            current.DisplayName = "After";
            current.ModelProviderId = updatedModelProviderId;
            current.SummaryModelProviderId = updatedSummaryModelProviderId;
        });

        Assert.Equal(originalId, agent.Id);
        Assert.Equal("original-name", agent.Name);
        Assert.Equal("original-prompt", agent.SystemPrompt);
        Assert.Same(originalTools, agent.Tools);
        Assert.True(agent.EnableSummary);
        Assert.Equal(AgentType.External, agent.Type);
        Assert.Equal("After", agent.DisplayName);
        Assert.Equal(updatedModelProviderId, agent.ModelProviderId);
        Assert.Equal(updatedSummaryModelProviderId, agent.SummaryModelProviderId);
        Assert.Equal("creator", agent.CreateBy);
        Assert.Equal(originalCreateTime, agent.CreateTime);
        Assert.Null(agent.UpdateBy);
        Assert.Null(agent.UpdateTime);
    }

    [Fact]
    public void ApplyUpdate_ExternalAgentWithValidExtra_UpdatesNormalizedExtra()
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "external-agent",
            Type = AgentType.External,
            Extra = "{\"before\":true}",
        };

        new AgentBehavior(agent).ApplyUpdate(current => current.Extra = "  {\"sandbox\":false}  ");

        Assert.Equal("{\"sandbox\":false}", agent.Extra);
    }

    [Fact]
    public void ApplyUpdate_ExternalAgentWithBlankExtra_ClearsExtra()
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "external-agent",
            Type = AgentType.External,
            Extra = "{\"before\":true}",
        };

        new AgentBehavior(agent).ApplyUpdate(current => current.Extra = "   ");

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
            Id = Guid.CreateVersion7(),
            Name = "external-agent",
            Type = AgentType.External,
        };

        var exception = Assert.Throws<AgwException>(() =>
            new AgentBehavior(agent).ApplyUpdate(current => current.Extra = extra)
        );

        Assert.Equal(ErrorCodes.InvalidAgentExtraSettings.Code, exception.Code);
    }

    [Fact]
    public void ApplyUpdate_SystemAgent_PreservesExtra()
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            Type = AgentType.System,
            ModelProviderId = Guid.CreateVersion7(),
            Extra = "{\"managed\":true}",
        };

        new AgentBehavior(agent).ApplyUpdate(current => current.Extra = "{\"managed\":false}");

        Assert.Equal("{\"managed\":true}", agent.Extra);
    }

    [Fact]
    public void ApplyUpdate_SystemAgent_UpdatesEnvironmentVariables()
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            Type = AgentType.System,
            ModelProviderId = Guid.CreateVersion7(),
            EnvironmentVariables = new Dictionary<string, string> { ["BEFORE"] = "value" },
        };

        new AgentBehavior(agent).ApplyUpdate(current =>
            current.EnvironmentVariables = new Dictionary<string, string> { ["AFTER"] = "" }
        );

        Assert.Single(agent.EnvironmentVariables);
        Assert.Equal("", agent.EnvironmentVariables["AFTER"]);
    }
}
