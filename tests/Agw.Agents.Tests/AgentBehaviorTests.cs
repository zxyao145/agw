using System.Text.Json;
using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Tooling;

namespace Agw.Agents.Tests;

public class AgentBehaviorTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PrepareForCreate_A2AWithoutEndpoint_ThrowsInvalidParam()
    {
        var behavior = new AgentBehavior(new Agent { Type = AgentType.External, ExternalAgentKind = EngineKind.A2A });

        Assert.Equal(ErrorCodes.InvalidParam.Code, Assert.Throws<AgwException>(() => behavior.PrepareForCreate()).Code);
    }

    [Fact]
    public void PrepareForCreate_CodexWithA2AEndpoint_ThrowsInvalidParam()
    {
        var behavior = new AgentBehavior(new Agent { Type = AgentType.External, ExternalAgentKind = EngineKind.Codex });

        Assert.Equal(
            ErrorCodes.InvalidParam.Code,
            Assert.Throws<AgwException>(() => behavior.PrepareForCreate(new("https://a.example/card", null))).Code
        );
    }

    [Theory]
    [InlineData(AgentUpdateField.ModelProviderId)]
    [InlineData(AgentUpdateField.Extra)]
    [InlineData(AgentUpdateField.EnvironmentVariables)]
    [InlineData(AgentUpdateField.ResponseSchema)]
    public void PrepareForCreate_A2AWithUnsupportedSetting_ThrowsInvalidParam(AgentUpdateField setting)
    {
        var agent = new Agent
        {
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.A2A,
            ModelProviderId = setting == AgentUpdateField.ModelProviderId ? Guid.NewGuid() : null,
            Extra = setting == AgentUpdateField.Extra ? """{"a":1}""" : null,
            EnvironmentVariables =
                setting == AgentUpdateField.EnvironmentVariables ? new() { ["ENV"] = "value" } : new(),
            ResponseSchema = setting == AgentUpdateField.ResponseSchema ? "{}" : null,
        };

        var error = Assert.Throws<AgwException>(() =>
            new AgentBehavior(agent).PrepareForCreate(new("https://a.example/card", null))
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("{}")]
    public void PrepareForCreate_A2AWithEmptyExtra_StoresEmptyObject(string? extra)
    {
        var agent = new Agent
        {
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.A2A,
            Extra = extra,
        };

        new AgentBehavior(agent).PrepareForCreate(new("https://a.example/card", null));

        Assert.Equal("{}", agent.Extra);
    }

    [Fact]
    public void EnsureUpdateAllowed_A2AWithExtra_ThrowsInvalidParam()
    {
        var behavior = new AgentBehavior(new Agent { Type = AgentType.External, ExternalAgentKind = EngineKind.A2A });
        var update = new AgentUpdate
        {
            SpecifiedFields = new HashSet<AgentUpdateField> { AgentUpdateField.Extra },
            Extra = """{"a":1}""",
        };

        Assert.Equal(
            ErrorCodes.InvalidParam.Code,
            Assert.Throws<AgwException>(() => behavior.EnsureUpdateAllowed(update)).Code
        );
    }

    [Fact]
    public void SetA2AEndpoint_ExistingEndpoint_ReplacesAllValues()
    {
        var agent = new Agent
        {
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.A2A,
            Id = Guid.NewGuid(),
        };
        var behavior = new AgentBehavior(agent);
        var original = new A2AEndpointSnapshot
        {
            CardUrl = "https://a.example/card",
            Credentials = new Dictionary<string, string>
            {
                ["https://a.example:443"] = """{"Authorization":"value"}""",
            },
            Interface = new("https://a.example/agent", "JSONRPC"),
            Streaming = false,
            Extra = new AgentA2AEndpointExtra
            {
                Card = JsonSerializer.SerializeToElement(new { name = "old" }),
                CardFetchedAt = UtcNow,
            },
        };
        behavior.SetA2AEndpoint(original);
        var previousRevision = Assert.IsType<AgentA2AEndpoint>(agent.A2AEndpoint).InterfaceRevision;
        Assert.NotEqual(Guid.Empty, previousRevision);

        var replacement = new A2AEndpointSnapshot
        {
            CardUrl = "https://b.example/card",
            Credentials = [],
            Interface = new("https://b.example/agent", "HTTP+JSON"),
            Streaming = true,
            Extra = new AgentA2AEndpointExtra
            {
                Card = JsonSerializer.SerializeToElement(new { name = "new" }),
                CardFetchedAt = UtcNow.AddSeconds(1),
            },
        };
        behavior.SetA2AEndpoint(replacement);

        var endpoint = Assert.IsType<AgentA2AEndpoint>(agent.A2AEndpoint);
        Assert.Equal(agent.Id, endpoint.AgentId);
        Assert.Equal(replacement.CardUrl, endpoint.CardUrl);
        Assert.Equal(replacement.Interface.Url, endpoint.InterfaceUrl);
        Assert.Equal(replacement.Interface.ProtocolBinding, endpoint.ProtocolBinding);
        Assert.NotEqual(previousRevision, endpoint.InterfaceRevision);
        Assert.True(endpoint.Streaming);
        Assert.Empty(endpoint.Credentials);
        Assert.Equal("new", endpoint.Extra.Card.GetProperty("name").GetString());
        Assert.Equal(replacement.Extra.CardFetchedAt, endpoint.Extra.CardFetchedAt);
    }

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
    public void PrepareForCreate_SystemAgentWithExternalKind_ThrowsAgwException()
    {
        var agent = new Agent
        {
            Type = AgentType.System,
            ExternalAgentKind = EngineKind.Codex,
            ModelProviderId = Guid.CreateVersion7(),
        };

        var exception = Assert.Throws<AgwException>(() => new AgentBehavior(agent).PrepareForCreate());

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public void PrepareForCreate_ExternalAgentWithoutSupportedKind_ThrowsAgwException()
    {
        var agent = new Agent { Type = AgentType.External };

        var exception = Assert.Throws<AgwException>(() => new AgentBehavior(agent).PrepareForCreate());

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Theory]
    [InlineData(EngineKind.ClaudeCode, true)]
    [InlineData(EngineKind.Codex, true)]
    [InlineData(EngineKind.Pi, true)]
    [InlineData(EngineKind.Pi, false)]
    public void PrepareForCreate_ExternalAgentWithSummaryConfiguration_ThrowsInvalidParam(
        EngineKind kind,
        bool enableSummary
    )
    {
        var summaryModelProviderId = Guid.CreateVersion7();
        var agent = new Agent
        {
            Type = AgentType.External,
            ExternalAgentKind = kind,
            EnableSummary = enableSummary,
            ModelProviderId = null,
            SummaryModelProviderId = summaryModelProviderId,
        };

        var exception = Assert.Throws<AgwException>(() => new AgentBehavior(agent).PrepareForCreate());
        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public void PrepareForCreate_ExternalAgentWithSummaryEnabledWithoutModelProvider_ThrowsAgwException()
    {
        var agent = new Agent
        {
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.ClaudeCode,
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
            new AgentBehavior(agent).ApplyUpdate(CreateSystemUpdate(modelProviderId: null))
        );

        Assert.Equal(ErrorCodes.SystemAgentRequiresModelProvider.Code, exception.Code);
    }

    [Fact]
    public void ApplyUpdate_SystemAgentMissingRequiredFields_ThrowsInvalidParamWithoutChange()
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            DisplayName = "Before",
            Type = AgentType.System,
            ModelProviderId = Guid.CreateVersion7(),
        };

        var exception = Assert.Throws<AgwException>(() =>
            new AgentBehavior(agent).ApplyUpdate(
                new AgentUpdate
                {
                    SpecifiedFields = new HashSet<AgentUpdateField> { AgentUpdateField.DisplayName },
                    DisplayName = "After",
                }
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains("systemPrompt", exception.Message, StringComparison.Ordinal);
        Assert.Equal("Before", agent.DisplayName);
    }

    [Fact]
    public void ApplyUpdate_ExternalAgentWithRuntimeFields_ThrowsInvalidParamWithoutChange()
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "external-agent",
            DisplayName = "Before",
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.ClaudeCode,
        };

        var exception = Assert.Throws<AgwException>(() =>
            new AgentBehavior(agent).ApplyUpdate(
                new AgentUpdate
                {
                    SpecifiedFields = new HashSet<AgentUpdateField>
                    {
                        AgentUpdateField.DisplayName,
                        AgentUpdateField.SystemPrompt,
                        AgentUpdateField.Tools,
                    },
                    DisplayName = "After",
                    SystemPrompt = "updated-prompt",
                    Tools = [new ToolValue { Definition = new WebFetchToolDefinition() }],
                }
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains("systemPrompt, tools", exception.Message, StringComparison.Ordinal);
        Assert.Equal("Before", agent.DisplayName);
    }

    [Fact]
    public void ApplyUpdate_PiAgentWithResponseSchema_ThrowsInvalidParam()
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "pi-agent",
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.Pi,
        };

        var exception = Assert.Throws<AgwException>(() =>
            new AgentBehavior(agent).ApplyUpdate(
                new AgentUpdate
                {
                    SpecifiedFields = new HashSet<AgentUpdateField> { AgentUpdateField.ResponseSchema },
                    ResponseSchema = "{\"type\":\"object\"}",
                }
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
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
            ExternalAgentKind = EngineKind.ClaudeCode,
            DisplayName = "Before",
            CreateBy = "creator",
            CreateTime = originalCreateTime,
        };
        var originalTools = agent.Tools;
        var updatedModelProviderId = Guid.CreateVersion7();

        new AgentBehavior(agent).ApplyUpdate(
            new AgentUpdate
            {
                SpecifiedFields = new HashSet<AgentUpdateField>
                {
                    AgentUpdateField.DisplayName,
                    AgentUpdateField.ModelProviderId,
                },
                DisplayName = "After",
                ModelProviderId = updatedModelProviderId,
            }
        );

        Assert.Equal(originalId, agent.Id);
        Assert.Equal("original-name", agent.Name);
        Assert.Equal("original-prompt", agent.SystemPrompt);
        Assert.Same(originalTools, agent.Tools);
        Assert.False(agent.EnableSummary);
        Assert.Equal(AgentType.External, agent.Type);
        Assert.Equal(EngineKind.ClaudeCode, agent.ExternalAgentKind);
        Assert.Equal("After", agent.DisplayName);
        Assert.Equal(updatedModelProviderId, agent.ModelProviderId);
        Assert.Null(agent.SummaryModelProviderId);
        Assert.Equal("creator", agent.CreateBy);
        Assert.Equal(originalCreateTime, agent.CreateTime);
        Assert.Null(agent.UpdateBy);
        Assert.Null(agent.UpdateTime);
    }

    [Fact]
    public void ApplyUpdate_ExternalAgent_AppliesValidatedOpaqueExtra()
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "external-agent",
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.ClaudeCode,
            Extra = "{\"before\":true}",
        };

        new AgentBehavior(agent).ApplyUpdate(
            new AgentUpdate
            {
                SpecifiedFields = new HashSet<AgentUpdateField> { AgentUpdateField.Extra },
                Extra = "{\"sandbox\":false}",
            }
        );

        Assert.Equal("{\"sandbox\":false}", agent.Extra);
    }

    [Fact]
    public void ApplyUpdate_SystemAgent_PreservesExtra()
    {
        var modelProviderId = Guid.CreateVersion7();
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            Type = AgentType.System,
            ModelProviderId = modelProviderId,
            Extra = "{\"managed\":true}",
        };

        new AgentBehavior(agent).ApplyUpdate(
            CreateSystemUpdate(modelProviderId, AgentUpdateField.Extra) with
            {
                Extra = "{\"managed\":false}",
            }
        );

        Assert.Equal("{\"managed\":true}", agent.Extra);
    }

    [Fact]
    public void ApplyUpdate_SystemAgent_UpdatesEnvironmentVariables()
    {
        var modelProviderId = Guid.CreateVersion7();
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            Type = AgentType.System,
            ModelProviderId = modelProviderId,
            EnvironmentVariables = new Dictionary<string, string> { ["BEFORE"] = "value" },
        };

        new AgentBehavior(agent).ApplyUpdate(
            CreateSystemUpdate(modelProviderId, AgentUpdateField.EnvironmentVariables) with
            {
                EnvironmentVariables = new Dictionary<string, string> { ["AFTER"] = "" },
            }
        );

        Assert.Single(agent.EnvironmentVariables);
        Assert.Equal("", agent.EnvironmentVariables["AFTER"]);
    }

    [Fact]
    public void PrepareForCreate_DuplicateToolNames_ThrowsInvalidParam()
    {
        var agent = new Agent
        {
            Type = AgentType.System,
            ModelProviderId = Guid.CreateVersion7(),
            Tools =
            [
                new ToolValue { Definition = new WebFetchToolDefinition() },
                new ToolValue { Definition = new WebFetchToolDefinition() },
            ],
        };

        var exception = Assert.Throws<AgwException>(() => new AgentBehavior(agent).PrepareForCreate());

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Theory]
    [InlineData(AgentType.System, true)]
    [InlineData(AgentType.External, false)]
    public void UpdatesResourceBindings_AgentType_OnlySystemAgentsReplaceBindings(AgentType type, bool expected)
    {
        var agent = new Agent { Type = type };

        Assert.Equal(expected, new AgentBehavior(agent).UpdatesResourceBindings());
    }

    private static AgentUpdate CreateSystemUpdate(Guid? modelProviderId, params AgentUpdateField[] additionalFields) =>
        new()
        {
            SpecifiedFields = new HashSet<AgentUpdateField>([
                AgentUpdateField.DisplayName,
                AgentUpdateField.Description,
                AgentUpdateField.SystemPrompt,
                AgentUpdateField.ModelProviderId,
                .. additionalFields,
            ]),
            DisplayName = "System Agent",
            Description = "Handles system work.",
            SystemPrompt = "You are helpful.",
            ModelProviderId = modelProviderId,
        };
}
