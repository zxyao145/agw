using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agents;

using Microsoft.EntityFrameworkCore;

using Xunit;

namespace Agw.Host.Tests;

public class AgentflowNodeExecutionTraceModelTests
{
    [Fact]
    public void NodeKind_UsesStringConversion()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var dbContext = new AgwDbContext(options);

        var entityType = dbContext.Model.FindEntityType(typeof(AgentflowTrace));
        Assert.NotNull(entityType);
        var property = entityType.FindProperty(nameof(AgentflowTrace.NodeKind));
        Assert.NotNull(property);

        Assert.Equal(typeof(string), property.GetProviderClrType());
        Assert.Equal(32, property.GetMaxLength());
    }
}
