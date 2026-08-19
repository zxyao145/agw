using Agw.Agents.Definitions.Contracts;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public class AgentflowEnableRemovalTests
{
    [Fact]
    public void AgentflowContracts_DoNotExposeEnable()
    {
        Assert.Null(typeof(Agentflow).GetProperty("Enable"));
        Assert.Null(typeof(AgentflowCreateRequest).GetProperty("Enable"));
        Assert.Null(typeof(AgentflowUpdateRequest).GetProperty("Enable"));
    }

    [Fact]
    public void Model_Agentflow_DoesNotMapEnable()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("Data Source=:memory:")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var dbContext = new AgwDbContext(options);

        var agentflow = dbContext.Model.FindEntityType(typeof(Agentflow));

        Assert.NotNull(agentflow);
        Assert.Null(agentflow.FindProperty("Enable"));
    }
}
