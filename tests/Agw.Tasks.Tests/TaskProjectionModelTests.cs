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

    [Fact]
    public void TaskSessionBinding_HasUniqueProjectContextAgentIndex()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        using var dbContext = new AgwDbContext(options);
        var entityType = dbContext.Model.FindEntityType(typeof(TaskSessionBinding));
        Assert.NotNull(entityType);

        Assert.Null(entityType.FindProperty("TaskId"));

        var contextIdProperty = entityType.FindProperty(nameof(TaskSessionBinding.ProjectContextId));
        var agentIdProperty = entityType.FindProperty(nameof(TaskSessionBinding.AgentId));
        var externalAgentNameProperty = entityType.FindProperty(nameof(TaskSessionBinding.ExternalAgentName));
        Assert.NotNull(contextIdProperty);
        Assert.NotNull(agentIdProperty);
        Assert.NotNull(externalAgentNameProperty);

        var bindingIndex = entityType.GetIndexes().Single(index =>
            index.Properties.SequenceEqual([contextIdProperty, agentIdProperty, externalAgentNameProperty]));

        Assert.True(bindingIndex.IsUnique);
    }

    [Fact]
    public void TaskRecord_HasContextConversationSequenceIndex()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        using var dbContext = new AgwDbContext(options);
        var entityType = dbContext.Model.FindEntityType(typeof(TaskRecord));
        Assert.NotNull(entityType);

        var contextIdProperty = entityType.FindProperty(nameof(TaskRecord.ProjectContextId));
        var conversationSequenceProperty = entityType.FindProperty(nameof(TaskRecord.ConversationSequence));
        Assert.NotNull(contextIdProperty);
        Assert.NotNull(conversationSequenceProperty);

        Assert.Contains(
            entityType.GetIndexes(),
            index => index.Properties.SequenceEqual([contextIdProperty, conversationSequenceProperty]));
    }

    [Fact]
    public void SharedTaskContracts_DoNotExposeInternalTaskResponseTypes()
    {
        var contractTypeNames = typeof(TaskCreateRequest).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "Agw.Shared.Contracts.Tasks")
            .Select(type => type.Name)
            .ToArray();

        Assert.DoesNotContain("TaskResponse", contractTypeNames);
        Assert.DoesNotContain("TaskSummaryResponse", contractTypeNames);
    }
}
