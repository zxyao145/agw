using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Tasks;

using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Tests;

public class ProjectTaskModelTests
{
    [Fact]
    public void ContextIdIndexIsNotUnique()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        using var dbContext = new AgwDbContext(options);
        var entityType = dbContext.Model.FindEntityType(typeof(ProjectTask));
        Assert.NotNull(entityType);

        var contextIdProperty = entityType.FindProperty(nameof(ProjectTask.ContextId));
        Assert.NotNull(contextIdProperty);

        var contextIdIndex = entityType.GetIndexes().Single(index =>
            index.Properties.Count == 1 && index.Properties[0] == contextIdProperty);

        Assert.False(contextIdIndex.IsUnique);
    }
}
