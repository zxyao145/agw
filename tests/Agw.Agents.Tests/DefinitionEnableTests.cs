using Agw.Agents.Definitions.Contracts;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed class DefinitionEnableTests
{
    [Fact]
    public void Definitions_DefaultToEnabledAndExposeEnableInAgentResponse()
    {
        // Arrange
        var agent = new Agent();
        var agentflow = new Agentflow();

        // Act
        var response = AgentResponse.FromDomain(agent);

        // Assert
        Assert.True(agent.Enable);
        Assert.True(agentflow.Enable);
        Assert.True(response.Enable);
    }

    [Fact]
    public void Model_DefinitionsMapEnableWithTrueDatabaseDefault()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("Data Source=:memory:")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var dbContext = new AgwDbContext(options);

        // Act
        var agent = dbContext.Model.FindEntityType(typeof(Agent));
        var agentflow = dbContext.Model.FindEntityType(typeof(Agentflow));

        // Assert
        Assert.NotNull(agent);
        Assert.NotNull(agentflow);
        Assert.True((bool?)agent.FindProperty(nameof(Agent.Enable))?.GetDefaultValue());
        Assert.True((bool?)agentflow.FindProperty(nameof(Agentflow.Enable))?.GetDefaultValue());
    }
}
