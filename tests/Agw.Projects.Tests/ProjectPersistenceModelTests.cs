using Agw.Infrastructure.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Agw.Projects.Tests;

public class ProjectPersistenceModelTests
{
    [Fact]
    public void Model_ProjectCapabilities_ConfiguresPropertiesAndRelations()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("Data Source=:memory:")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var dbContext = new AgwDbContext(options);

        var project = dbContext.Model.FindEntityType(typeof(Agw.Shared.Data.Entities.Projects.Project));
        Assert.NotNull(project);
        Assert.Equal(4000, project.FindProperty("Tools")!.GetMaxLength());
        Assert.NotNull(project.FindProperty("EnvironmentVariables")!.GetValueConverter());

        AssertRelation(dbContext.Model, "project_skill_relation", "SkillId");
        AssertRelation(dbContext.Model, "project_mcp_server_relation", "McpToolServerId");
        AssertRelation(dbContext.Model, "project_app_relation", "AppInstanceId");
    }

    private static void AssertRelation(IModel model, string tableName, string relatedIdPropertyName)
    {
        var relation = model.GetEntityTypes().SingleOrDefault(entity => entity.GetTableName() == tableName);
        Assert.NotNull(relation);
        Assert.Equal(
            ["ProjectId", relatedIdPropertyName],
            relation.FindPrimaryKey()!.Properties.Select(property => property.Name).ToArray());
        Assert.Contains(
            relation.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual([relatedIdPropertyName]));
        Assert.All(relation.GetForeignKeys(), foreignKey => Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior));
    }
}
