using System.Text.Json;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Definitions.Domain.Decisions;
using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Tests;

public class AgentRequestsTests
{
    [Theory]
    [InlineData(EngineKind.Maf, false)]
    [InlineData(EngineKind.Maf, true)]
    [InlineData(EngineKind.ClaudeCode, true)]
    [InlineData(EngineKind.Codex, true)]
    [InlineData(EngineKind.Pi, true)]
    [InlineData(EngineKind.Pi, false)]
    public void FromDomain_AgentResponses_ReportEffectiveSummaryWithoutChangingStoredConfiguration(
        EngineKind kind,
        bool enableSummary
    )
    {
        // Arrange
        var summaryModelProviderId = Guid.CreateVersion7();
        var agent = new Agent
        {
            Type = kind == EngineKind.Maf ? AgentType.System : AgentType.External,
            ExternalAgentKind = kind,
            EnableSummary = enableSummary,
            SummaryModelProviderId = summaryModelProviderId,
        };

        // Act
        var detail = AgentResponse.FromDomain(agent);
        var list = AgentListResponse.FromDomain(agent);

        // Assert
        var expectedEnabled = agent.Type == AgentType.System && enableSummary;
        Guid? expectedProvider = agent.Type == AgentType.System ? summaryModelProviderId : null;
        Assert.Equal(expectedEnabled, detail.EnableSummary);
        Assert.Equal(expectedEnabled, list.EnableSummary);
        Assert.Equal(expectedProvider, detail.SummaryModelProviderId);
        Assert.Equal(expectedProvider, list.SummaryModelProviderId);
        Assert.Equal(enableSummary, agent.EnableSummary);
        Assert.Equal(summaryModelProviderId, agent.SummaryModelProviderId);
    }

    [Fact]
    public void AgentUpdateRequest_StoresExternalAgentExtraSettings()
    {
        var request = new AgentUpdateRequest
        {
            DisplayName = "External Agent",
            Description = "Description",
            SystemPrompt = "",
            ModelProviderId = null,
            Extra = "{\"sandbox\":false}",
        };

        Assert.Equal("{\"sandbox\":false}", request.Extra);
    }

    [Fact]
    public void AgentCreateAndUpdateRequests_StoreEnvironmentVariables()
    {
        var environmentVariables = new Dictionary<string, string> { ["AGW_TOKEN"] = "secret" };
        var createRequest = new AgentCreateRequest(
            "Agent",
            "agent",
            "Description",
            "Prompt",
            Guid.CreateVersion7(),
            EnvironmentVariables: environmentVariables
        );
        var updateRequest = new AgentUpdateRequest
        {
            DisplayName = "Agent",
            Description = "Description",
            SystemPrompt = "Prompt",
            ModelProviderId = Guid.CreateVersion7(),
            EnvironmentVariables = environmentVariables,
        };

        Assert.Same(environmentVariables, createRequest.EnvironmentVariables);
        Assert.Same(environmentVariables, updateRequest.EnvironmentVariables);
    }

    [Fact]
    public void AgentResponse_FromDomain_ExposesEnvironmentVariables()
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Type = AgentType.System,
            EnvironmentVariables = new Dictionary<string, string> { ["AGW_TOKEN"] = "secret" },
        };

        var response = AgentResponse.FromDomain(agent);

        Assert.Equal("secret", response.EnvironmentVariables["AGW_TOKEN"]);
    }

    [Fact]
    public void ExternalAgentKind_RoundTripsThroughCreateRequestAndResponse()
    {
        var createRequest = new AgentCreateRequest(
            "Reviewer",
            "reviewer",
            "Reviews changes",
            "",
            null,
            Type: AgentType.External,
            ExternalAgentKind: EngineKind.ClaudeCode,
            Extra: "{\"model\":\"claude-sonnet\"}"
        );
        var response = AgentResponse.FromDomain(
            new Agent
            {
                Type = AgentType.External,
                ExternalAgentKind = EngineKind.ClaudeCode,
                Extra = createRequest.Extra,
            }
        );

        Assert.Equal(AgentType.External, createRequest.Type);
        Assert.Equal(EngineKind.ClaudeCode, createRequest.ExternalAgentKind);
        Assert.Equal(EngineKind.ClaudeCode, response.ExternalAgentKind);
        Assert.Equal(createRequest.Extra, response.Extra);
    }

    [Fact]
    public void AgentCreateRequest_DeserializationWithoutNewFields_PreservesSystemDefaults()
    {
        var request = JsonSerializer.Deserialize<AgentCreateRequest>(
            """
            {
              "displayName": "System Agent",
              "name": "system-agent",
              "description": "",
              "systemPrompt": "Help the user.",
              "modelProviderId": null
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );

        Assert.NotNull(request);
        Assert.Null(request.Type);
        Assert.Null(request.ExternalAgentKind);
        Assert.Null(request.Extra);
    }

    [Fact]
    public void AgentSummaryModelProviderId_RoundTripsThroughRequestsAndResponse()
    {
        var summaryModelProviderId = Guid.CreateVersion7();
        var createRequest = new AgentCreateRequest(
            "Agent",
            "agent",
            "Description",
            "Prompt",
            Guid.CreateVersion7(),
            SummaryModelProviderId: summaryModelProviderId
        );
        var updateRequest = new AgentUpdateRequest
        {
            DisplayName = "Agent",
            Description = "Description",
            SystemPrompt = "Prompt",
            ModelProviderId = Guid.CreateVersion7(),
            SummaryModelProviderId = summaryModelProviderId,
        };
        var response = AgentResponse.FromDomain(new Agent { SummaryModelProviderId = summaryModelProviderId });

        Assert.Equal(summaryModelProviderId, createRequest.SummaryModelProviderId);
        Assert.Equal(summaryModelProviderId, updateRequest.SummaryModelProviderId);
        Assert.Equal(summaryModelProviderId, response.SummaryModelProviderId);
    }

    [Fact]
    public void AgentUpdateRequest_Deserialization_TracksOmittedAndSpecifiedFields()
    {
        var request = JsonSerializer.Deserialize<AgentUpdateRequest>(
            """
            {
              "displayName": "External Agent",
              "modelProviderId": null,
              "extra": null,
              "environmentVariables": null
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );

        var command = Assert.IsType<AgentUpdateCommand>(request?.ToCommand());
        Assert.True(command.IsSpecified(AgentUpdateField.DisplayName));
        Assert.False(command.IsSpecified(AgentUpdateField.Description));
        Assert.True(command.IsSpecified(AgentUpdateField.ModelProviderId));
        Assert.True(command.IsSpecified(AgentUpdateField.Extra));
        Assert.True(command.IsSpecified(AgentUpdateField.EnvironmentVariables));
        Assert.Null(command.ModelProviderId);
        Assert.Null(command.Extra);
        Assert.Null(command.EnvironmentVariables);
    }
}
