using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Projects;

using Microsoft.EntityFrameworkCore;

using Xunit;

namespace Agw.Host.Tests;

public class AgentUsageModelTests
{
    [Fact]
    public void AgentUsage_MapsStandaloneFactTableWithRequiredIndexes()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var dbContext = new AgwDbContext(options);

        var entityType = dbContext.Model.FindEntityType(typeof(AgentUsage));
        Assert.NotNull(entityType);
        Assert.Equal("agent_usage", entityType.GetTableName());
        Assert.Equal(64, entityType.FindProperty(nameof(AgentUsage.ContextId))!.GetMaxLength());
        Assert.Equal(200, entityType.FindProperty(nameof(AgentUsage.AgentName))!.GetMaxLength());
        Assert.Empty(entityType.GetForeignKeys());

        var indexes = entityType.GetIndexes()
            .Select(index => index.Properties.Select(property => property.Name).ToArray())
            .ToList();
        Assert.Contains(indexes, properties => properties.SequenceEqual(
            [nameof(AgentUsage.ProjectId), nameof(AgentUsage.ContextId)]));
        Assert.Contains(indexes, properties => properties.SequenceEqual([nameof(AgentUsage.AgentName)]));
        Assert.Contains(indexes, properties => properties.SequenceEqual([nameof(AgentUsage.RecordedAt)]));

        var projectContextType = dbContext.Model.FindEntityType(typeof(ProjectContext));
        Assert.NotNull(projectContextType);
        Assert.Null(projectContextType.FindComplexProperty("Usage"));
    }
}
