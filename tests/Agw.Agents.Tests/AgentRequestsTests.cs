using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;

using Agw.Agents.Definitions.Contracts;

namespace Agw.Agents.Tests;

public class AgentRequestsTests
{
    [Fact]
    public void AgentUpdateRequest_StoresExternalAgentExtraSettings()
    {
        var request = new AgentUpdateRequest(
            "External Agent",
            "Description",
            "",
            null,
            Extra: "{\"sandbox\":false}");

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
        var updateRequest = new AgentUpdateRequest(
            "Agent",
            "Description",
            "Prompt",
            Guid.NewGuid(),
            EnvironmentVariables: environmentVariables);

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
}
