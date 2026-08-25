using Agw.Agents.Definitions.Contracts;

namespace Agw.Projects.Tests;

public class ConnectionBindingContractTests
{
    [Theory]
    [InlineData(typeof(AgentCreateRequest))]
    [InlineData(typeof(AgentUpdateRequest))]
    [InlineData(typeof(ProjectCreateRequest))]
    [InlineData(typeof(ProjectUpdateRequest))]
    public void RequestContract_UsesConnectionIdsAndRemovesAppInstanceIds(Type contractType)
    {
        Assert.NotNull(contractType.GetProperty("ConnectionIds"));
        Assert.Null(contractType.GetProperty("AppInstanceIds"));
    }

    [Fact]
    public void AgentResponse_UsesConnectionRelationsAndRemovesAppRelations()
    {
        Assert.NotNull(typeof(AgentResponse).GetProperty("AgentConnectionRelations"));
        Assert.Null(typeof(AgentResponse).GetProperty("AgentAppRelations"));
    }

    [Fact]
    public void ProjectResponse_UsesConnectionRelationsAndRemovesAppRelations()
    {
        Assert.NotNull(typeof(ProjectResponse).GetProperty("ProjectConnectionRelations"));
        Assert.Null(typeof(ProjectResponse).GetProperty("ProjectAppRelations"));
    }
}
