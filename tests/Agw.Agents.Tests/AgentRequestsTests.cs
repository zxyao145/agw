using System.Text.Json;

using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Tests;

public class AgentRequestsTests
{
    [Fact]
    public void AgentUpdateRequest_StoresExternalAgentExtraSettings()
    {
        var request = new AgentUpdateRequest
        {
            DisplayName = "External Agent",
            Description = "Description",
            SystemPrompt = "",
            ModelProviderId = null,
            Extra = "{\"sandbox\":false}"
        };

        Assert.Equal("{\"sandbox\":false}", request.Extra);
    }

    [Fact]
    public void AgentCreateAndUpdateRequests_StoreEnvironmentVariables()
    {
        var environmentVariables = new Dictionary<string, string>
        {
            ["AGW_TOKEN"] = "secret",
        };
        var createRequest = new AgentCreateRequest(
            "Agent",
            "agent",
            "Description",
            "Prompt",
            Guid.NewGuid(),
            EnvironmentVariables: environmentVariables);
        var updateRequest = new AgentUpdateRequest
        {
            DisplayName = "Agent",
            Description = "Description",
            SystemPrompt = "Prompt",
            ModelProviderId = Guid.NewGuid(),
            EnvironmentVariables = environmentVariables
        };

        Assert.Same(environmentVariables, createRequest.EnvironmentVariables);
        Assert.Same(environmentVariables, updateRequest.EnvironmentVariables);
    }

    [Fact]
    public void AgentResponse_FromDomain_ExposesEnvironmentVariables()
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Type = AgentType.System,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["AGW_TOKEN"] = "secret",
            },
        };

        var response = AgentResponse.FromDomain(agent);

        Assert.Equal("secret", response.EnvironmentVariables["AGW_TOKEN"]);
    }

    [Fact]
    public void AgentSummaryModelProviderId_RoundTripsThroughRequestsAndResponse()
    {
        var summaryModelProviderId = Guid.NewGuid();
        var createRequest = new AgentCreateRequest(
            "Agent",
            "agent",
            "Description",
            "Prompt",
            Guid.NewGuid(),
            SummaryModelProviderId: summaryModelProviderId);
        var updateRequest = new AgentUpdateRequest
        {
            DisplayName = "Agent",
            Description = "Description",
            SystemPrompt = "Prompt",
            ModelProviderId = Guid.NewGuid(),
            SummaryModelProviderId = summaryModelProviderId
        };
        var response = AgentResponse.FromDomain(new Agent
        {
            SummaryModelProviderId = summaryModelProviderId,
        });

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
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

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
