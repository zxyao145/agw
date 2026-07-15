using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
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
    public void ProviderAndSkillEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(Provider),
            typeof(ProviderAuthConfig),
            typeof(LlmModel),
            typeof(ModelProviderRelation),
            typeof(Skill));
    }

    [Fact]
    public void AgentAndToolEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(Agent),
            typeof(AgentAppRelation),
            typeof(AgentSkillRelation),
            typeof(McpServer),
            typeof(AgentMcpServerRelation));
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
            typeof(ProjectSkillRelation),
            typeof(ProjectMcpServerRelation),
            typeof(ProjectAppRelation),
            typeof(ProjectContext),
            typeof(TaskSessionBinding),
            typeof(TaskRecord));
    }

    [Fact]
    public void JobAndIntegrationEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(Job),
            typeof(JobLog),
            typeof(AppInstance),
            typeof(OAuthAuthorizationToken));
    }

    [Fact]
    public void PersistedEntities_AllDeclareMatchingConfigurations()
    {
        var entityTypes = typeof(Project).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<TableAttribute>() is not null)
            .OrderBy(type => type.FullName)
            .ToArray();

        Assert.Equal(26, entityTypes.Length);
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
