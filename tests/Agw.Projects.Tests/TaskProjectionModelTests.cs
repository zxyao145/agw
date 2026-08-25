using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class TaskProjectionModelTests
{
    [Fact]
    public void TaskProjection_IsNotEfEntity()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite("DataSource=:memory:").Options;

        using var dbContext = new AgwDbContext(options);
        var entityType = dbContext.Model.FindEntityType(typeof(TaskProjection));
        Assert.Null(entityType);
    }

    [Fact]
    public void ProjectConversation_HasExpectedTableAndUniqueProjectAndContextIdIndex()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("DataSource=:memory:")
            .UseSnakeCaseNamingConvention()
            .Options;

        using var dbContext = new AgwDbContext(options);
        var entityType = dbContext.Model.FindEntityType(typeof(ProjectConversation));
        Assert.NotNull(entityType);
        Assert.Equal("project_conversation", entityType.GetTableName());

        var projectIdProperty = entityType.FindProperty(nameof(ProjectConversation.ProjectId));
        var contextIdProperty = entityType.FindProperty(nameof(ProjectConversation.ContextId));
        Assert.NotNull(projectIdProperty);
        Assert.NotNull(contextIdProperty);

        var contextIdIndex = entityType
            .GetIndexes()
            .Single(index => index.Properties.SequenceEqual([projectIdProperty, contextIdProperty]));

        Assert.True(contextIdIndex.IsUnique);
    }

    [Fact]
    public void TaskSessionBinding_HasUniqueProjectConversationAgentIndex()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("DataSource=:memory:")
            .UseSnakeCaseNamingConvention()
            .Options;

        using var dbContext = new AgwDbContext(options);
        var entityType = dbContext.Model.FindEntityType(typeof(TaskSessionBinding));
        Assert.NotNull(entityType);

        Assert.Null(entityType.FindProperty("TaskId"));

        var contextIdProperty = entityType.FindProperty(nameof(TaskSessionBinding.ProjectConversationId));
        var agentIdProperty = entityType.FindProperty(nameof(TaskSessionBinding.AgentId));
        var externalAgentNameProperty = entityType.FindProperty(nameof(TaskSessionBinding.ExternalAgentName));
        Assert.NotNull(contextIdProperty);
        Assert.NotNull(agentIdProperty);
        Assert.NotNull(externalAgentNameProperty);
        Assert.Equal("project_conversation_id", contextIdProperty.GetColumnName());

        var bindingIndex = entityType
            .GetIndexes()
            .Single(index =>
                index.Properties.SequenceEqual([contextIdProperty, agentIdProperty, externalAgentNameProperty])
            );

        Assert.True(bindingIndex.IsUnique);
    }

    [Fact]
    public void ProjectConversationChatHistory_HasExpectedTableColumnAndConversationSequenceIndex()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("DataSource=:memory:")
            .UseSnakeCaseNamingConvention()
            .Options;

        using var dbContext = new AgwDbContext(options);
        var entityType = dbContext.Model.FindEntityType(typeof(ProjectConversationChatHistory));
        Assert.NotNull(entityType);
        Assert.Equal("project_conversation_chat_history", entityType.GetTableName());

        var contextIdProperty = entityType.FindProperty(nameof(ProjectConversationChatHistory.ConversationId));
        var conversationSequenceProperty = entityType.FindProperty(
            nameof(ProjectConversationChatHistory.ConversationSequence)
        );
        Assert.NotNull(contextIdProperty);
        Assert.NotNull(conversationSequenceProperty);
        Assert.Equal("project_conversation_id", contextIdProperty.GetColumnName());

        Assert.Contains(
            entityType.GetIndexes(),
            index => index.Properties.SequenceEqual([contextIdProperty, conversationSequenceProperty])
        );
    }

    [Fact]
    public void ProjectTaskContracts_DoNotExposeInternalTaskResponseTypes()
    {
        var contractTypeNames = typeof(TaskCreateRequest)
            .Assembly.GetTypes()
            .Where(type => type.Namespace == "Agw.Projects.Contracts")
            .Select(type => type.Name)
            .ToArray();

        Assert.DoesNotContain("TaskResponse", contractTypeNames);
        Assert.DoesNotContain("TaskSummaryResponse", contractTypeNames);
    }
}
