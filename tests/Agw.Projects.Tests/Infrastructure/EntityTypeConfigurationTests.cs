using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;

using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Tests;

public class EntityTypeConfigurationTests
{
    [Fact]
    public void GuidPrimaryKey_WhenGeneratedByEfCore_UsesVersion7()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new AgwDbContext(options);
        var authConfig = new ProviderAuthConfig();

        context.ProviderAuthConfigs.Add(authConfig);

        Assert.Equal(7, authConfig.Id.Version);
    }

    [Fact]
    public void ProviderAndSkillEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(Provider),
            typeof(ProviderAuthConfig),
            typeof(AgwAiModel),
            typeof(ModelProviderRelation),
            typeof(Skill),
            typeof(RemoteSkillCache));
    }

    [Fact]
    public void RemoteSkillCache_UsesSkillPrimaryKeyAndCascadeDelete()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new AgwDbContext(options);
        var entityType = context.Model.FindEntityType(typeof(RemoteSkillCache));

        Assert.NotNull(entityType);
        Assert.Equal(
            nameof(RemoteSkillCache.SkillId),
            Assert.Single(entityType.FindPrimaryKey()!.Properties).Name);
        var foreignKey = Assert.Single(entityType.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        Assert.Equal(typeof(Skill), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(
            2048,
            entityType.FindProperty(nameof(RemoteSkillCache.SourceUrl))!.GetMaxLength());
    }

    [Fact]
    public void AgentAndToolEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(Agent),
            typeof(AgentConnectionRelation),
            typeof(AgentSessionStateEntry),
            typeof(AgentSkillRelation),
            typeof(McpServer),
            typeof(AgentMcpServerRelation));
    }

    [Fact]
    public void AgentSessionState_UsesProjectConversationAgentAndNodeCompositeKey()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("Data Source=:memory:")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new AgwDbContext(options);

        var entityType = context.Model.FindEntityType(typeof(AgentSessionStateEntry))!;
        var primaryKey = entityType.FindPrimaryKey()!;
        var projectConversationIdProperty = entityType.FindProperty(
            nameof(AgentSessionStateEntry.ProjectConversationId))!;

        Assert.Equal(
            [
                nameof(AgentSessionStateEntry.ProjectConversationId),
                nameof(AgentSessionStateEntry.AgentId),
                nameof(AgentSessionStateEntry.AgentflowNodeId)
            ],
            primaryKey.Properties.Select(property => property.Name));
        Assert.Equal("project_conversation_id", projectConversationIdProperty.GetColumnName());

        var conversationForeignKey = Assert.Single(
            entityType.GetForeignKeys(),
            foreignKey => foreignKey.Properties.SequenceEqual([projectConversationIdProperty]));
        Assert.Equal(typeof(ProjectConversation), conversationForeignKey.PrincipalEntityType.ClrType);
    }

    [Fact]
    public void ProjectMemory_UsesProjectAndPathAsUniqueScope()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new AgwDbContext(options);
        var entityType = context.Model.FindEntityType(typeof(ProjectMemoryEntry))!;
        var uniqueIndex = Assert.Single(
            entityType.GetIndexes(),
            index => index.IsUnique);

        Assert.Equal(
            [
                nameof(ProjectMemoryEntry.ProjectId),
                nameof(ProjectMemoryEntry.Path)
            ],
            uniqueIndex.Properties.Select(property => property.Name));
    }

    [Fact]
    public void AgentflowAndObservableEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(Agentflow),
            typeof(AgentflowNode),
            typeof(AgentflowEdge),
            typeof(AgentflowTrace),
            typeof(AgentUsage));
    }

    [Fact]
    public void ProjectAndTaskEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(Project),
            typeof(ProjectMemoryEntry),
            typeof(ProjectSkillRelation),
            typeof(ProjectMcpServerRelation),
            typeof(ProjectConnectionRelation),
            typeof(ProjectConversation),
            typeof(TaskSessionBinding),
            typeof(ProjectConversationChatHistory));
    }

    [Fact]
    public void JobAndIntegrationEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(Job),
            typeof(JobLog),
            typeof(PluginInstallation),
            typeof(PluginInstallationCredential),
            typeof(Connection),
            typeof(ConnectionCredential));
    }

    [Fact]
    public void ExecutionEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(DurableExecutionRecord),
            typeof(DurableExecutionEventRecord));
    }

    [Fact]
    public void PersistedEntities_AllDeclareMatchingConfigurations()
    {
        var entityTypes = typeof(Project).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<TableAttribute>() is not null)
            .OrderBy(type => type.FullName)
            .ToArray();

        Assert.Equal(33, entityTypes.Length);
        AssertConfigured(entityTypes);
    }

    private static void AssertConfigured(params Type[] entityTypes)
    {
        var failures = entityTypes
            .Where(entityType =>
            {
                var attribute = entityType.GetCustomAttribute<EntityTypeConfigurationAttribute>();
                if (attribute is null)
                {
                    return true;
                }

                var expectedInterface = typeof(IEntityTypeConfiguration<>).MakeGenericType(entityType);
                return !expectedInterface.IsAssignableFrom(attribute.EntityTypeConfigurationType);
            })
            .Select(entityType => entityType.FullName)
            .ToArray();

        Assert.Empty(failures);
    }
}
