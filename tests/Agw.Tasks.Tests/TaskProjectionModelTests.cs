using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;

using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Tests;

public class TaskProjectionModelTests
{
    [Fact]
    public void TaskProjection_IsNotEfEntity()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        using var dbContext = new AgwDbContext(options);
        var entityType = dbContext.Model.FindEntityType(typeof(TaskProjection));
        Assert.Null(entityType);
    }

    [Fact]
    public void ProjectContext_HasUniqueProjectContextIdIndex()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        using var dbContext = new AgwDbContext(options);
        var entityType = dbContext.Model.FindEntityType(typeof(ProjectContext));
        Assert.NotNull(entityType);

        var projectIdProperty = entityType.FindProperty(nameof(ProjectContext.ProjectId));
        var contextIdProperty = entityType.FindProperty(nameof(ProjectContext.ContextId));
        Assert.NotNull(projectIdProperty);
        Assert.NotNull(contextIdProperty);

        var contextIdIndex = entityType.GetIndexes().Single(index =>
            index.Properties.SequenceEqual([projectIdProperty, contextIdProperty]));

        Assert.True(contextIdIndex.IsUnique);
    }
}
