using System.Security.Claims;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Auth;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Entities.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Tests;

public sealed class UserScopeFilterTests
{
    [Fact]
    public void UserScopeFilter_AllTableEntities_HaveNamedFilter()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var context = new AgwDbContext(options);

        // Act
        var unfilteredEntities = context
            .Model.GetEntityTypes()
            .Where(entityType => entityType.GetTableName() != null)
            .Where(entityType => entityType.FindDeclaredQueryFilter(UserScopeQueryFilterNames.UserScope) == null)
            .Select(entityType => entityType.ClrType.FullName ?? entityType.Name)
            .OrderBy(static entityName => entityName, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Empty(unfilteredEntities);
    }

    [Fact]
    public void ProjectDeletionInventory_AllDirectProjectScopedTables_HaveExplicitLifecycleCoverage()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var context = new AgwDbContext(options);
        var expectedDirectProjectScopedTypes = new[]
        {
            typeof(AgentSessionStateEntry),
            typeof(AgentUsage), // Intentional retention: usage is user-scoped analytics.
            typeof(AgentflowCheckpointRecord),
            typeof(AgentflowTrace),
            typeof(DurableExecutionRecord),
            typeof(Job),
            typeof(ProjectConnectionRelation),
            typeof(ProjectConversation),
            typeof(ProjectMcpServerRelation),
            typeof(ProjectMemoryEntry),
            typeof(ProjectSkillRelation),
            typeof(ProjectConversationBinding),
        };
        var expectedIndirectProjectScopedTypes = new[]
        {
            typeof(DurableExecutionEventRecord), // Deleted through matching durable execution IDs.
            typeof(JobLog), // Deleted through project Job IDs.
            typeof(ProjectConversationChatHistory), // Deleted through project Conversation IDs.
        };

        // Act
        var actualDirectProjectScopedTypes = context
            .Model.GetEntityTypes()
            .Where(entityType => entityType.GetTableName() != null)
            .Where(entityType =>
                entityType.GetProperties().Any(property => property.Name is "ProjectId" or "ProjectConversationId")
            )
            .Select(entityType => entityType.ClrType)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        var actualTableTypes = context
            .Model.GetEntityTypes()
            .Where(entityType => entityType.GetTableName() != null)
            .Select(entityType => entityType.ClrType)
            .ToHashSet();

        // Assert
        Assert.Equal(
            expectedDirectProjectScopedTypes.OrderBy(type => type.FullName, StringComparer.Ordinal),
            actualDirectProjectScopedTypes
        );
        Assert.All(expectedIndirectProjectScopedTypes, type => Assert.Contains(type, actualTableTypes));
    }

    [Fact]
    public async Task UserScopeFilter_AuthenticatedUser_SeesOnlyOwnedRoots()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
        var projectA = CreateProject("project-a", "user-a");
        var projectB = CreateProject("project-b", "user-b");
        var jobA = CreateJob("job-a", "user-a");
        var jobB = CreateJob("job-b", "user-b");

        await using (var seed = new AgwDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync(cancellationToken);
            seed.Projects.AddRange(projectA, projectB);
            seed.Jobs.AddRange(jobA, jobB);
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var context = new AgwDbContext(options);
        Assert.Empty(await context.Projects.ToListAsync(cancellationToken));
        Assert.Empty(await context.Jobs.ToListAsync(cancellationToken));

        using (UserInfoUtil.Push(CreatePrincipal("user-a")))
        {
            Assert.Equal("project-a", Assert.Single(await context.Projects.ToListAsync(cancellationToken)).Name);
            Assert.Equal("job-a", Assert.Single(await context.Jobs.ToListAsync(cancellationToken)).Name);
        }

        using (UserInfoUtil.Push(CreatePrincipal("user-b")))
        {
            Assert.Equal("project-b", Assert.Single(await context.Projects.ToListAsync(cancellationToken)).Name);
            Assert.Equal("job-b", Assert.Single(await context.Jobs.ToListAsync(cancellationToken)).Name);
            Assert.Null(await new EfRepository<Project>(context).GetByIdAsync(projectA.Id));
        }

        using (UserInfoUtil.Push(null))
        {
            Assert.Empty(await context.Projects.ToListAsync(cancellationToken));
            Assert.Empty(await context.Jobs.ToListAsync(cancellationToken));
        }

        using (UserInfoUtil.PushSystemScope())
        {
            Assert.Equal(2, await context.Projects.CountAsync(cancellationToken));
            Assert.Equal(2, await context.Jobs.CountAsync(cancellationToken));
        }
    }

    [Fact]
    public async Task UserScopeFilter_ChildRowsFollowTheirConsistencyRoot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;

        var projectA = CreateProject("project-a", "user-a");
        var projectB = CreateProject("project-b", "user-b");
        var jobA = CreateJob("job-a", "user-a");
        var jobB = CreateJob("job-b", "user-b");
        var agentA = CreateAgent("agent-a", "user-a");
        var agentB = CreateAgent("agent-b", "user-b");
        var skillA = CreateSkill("skill-a", "user-a");
        var skillB = CreateSkill("skill-b", "user-b");
        var flowA = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "flow-a",
            CreateBy = "user-a",
        };
        var flowB = new Agentflow
        {
            Id = Guid.CreateVersion7(),
            Name = "flow-b",
            CreateBy = "user-b",
        };
        var conversationA = new ProjectConversation
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectA.Id,
            ContextId = "context-a",
            CreateBy = "user-a",
        };
        var conversationB = new ProjectConversation
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectB.Id,
            ContextId = "context-b",
            CreateBy = "user-b",
        };

        await using (var seed = new AgwDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync(cancellationToken);
            seed.Projects.AddRange(projectA, projectB);
            seed.Jobs.AddRange(jobA, jobB);
            seed.Agents.AddRange(agentA, agentB);
            seed.Skills.AddRange(skillA, skillB);
            seed.AgentSkillRelations.AddRange(
                new AgentSkillRelation { AgentId = agentA.Id, SkillId = skillA.Id },
                new AgentSkillRelation { AgentId = agentB.Id, SkillId = skillB.Id }
            );
            seed.ProjectSkillRelations.AddRange(
                new ProjectSkillRelation { ProjectId = projectA.Id, SkillId = skillA.Id },
                new ProjectSkillRelation { ProjectId = projectB.Id, SkillId = skillB.Id }
            );
            seed.Agentflows.AddRange(flowA, flowB);
            seed.ProjectConversations.AddRange(conversationA, conversationB);
            seed.ProjectMemories.AddRange(
                new ProjectMemoryEntry
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = projectA.Id,
                    Path = "a",
                    Content = "a",
                },
                new ProjectMemoryEntry
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = projectB.Id,
                    Path = "b",
                    Content = "b",
                }
            );
            seed.ProjectConversationChatHistories.AddRange(
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = conversationA.Id,
                    TaskId = Guid.CreateVersion7(),
                },
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = conversationB.Id,
                    TaskId = Guid.CreateVersion7(),
                }
            );
            seed.JobLogs.AddRange(
                new JobLog
                {
                    Id = Guid.CreateVersion7(),
                    JobId = jobA.Id,
                    TaskId = Guid.CreateVersion7(),
                },
                new JobLog
                {
                    Id = Guid.CreateVersion7(),
                    JobId = jobB.Id,
                    TaskId = Guid.CreateVersion7(),
                }
            );
            seed.AgentflowNodeExecutionTraces.AddRange(
                new AgentflowTrace
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = projectA.Id,
                    ContextId = "context-a",
                    TaskId = Guid.CreateVersion7(),
                    AgentflowId = flowA.Id,
                    NodeId = "node-a",
                    Input = "a",
                },
                new AgentflowTrace
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = projectB.Id,
                    ContextId = "context-b",
                    TaskId = Guid.CreateVersion7(),
                    AgentflowId = flowB.Id,
                    NodeId = "node-b",
                    Input = "b",
                }
            );
            seed.AgentflowNodes.AddRange(
                new AgentflowNode { AgentflowId = flowA.Id, NodeId = "node-a" },
                new AgentflowNode { AgentflowId = flowB.Id, NodeId = "node-b" }
            );
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var context = new AgwDbContext(options);
        Assert.Null(await new EfRepository<ProjectConversation>(context).GetByIdAsync(conversationA.Id));
        using (UserInfoUtil.Push(CreatePrincipal("user-a")))
        {
            Assert.Single(await context.ProjectConversations.ToListAsync(cancellationToken));
            Assert.Single(await context.ProjectConversationChatHistories.ToListAsync(cancellationToken));
            Assert.Single(await context.ProjectMemories.ToListAsync(cancellationToken));
            Assert.Single(await context.JobLogs.ToListAsync(cancellationToken));
            Assert.Single(await context.AgentflowNodeExecutionTraces.ToListAsync(cancellationToken));
            Assert.Single(await context.AgentflowNodes.ToListAsync(cancellationToken));
            Assert.Single(await context.AgentSkillRelations.ToListAsync(cancellationToken));
            Assert.Single(await context.ProjectSkillRelations.ToListAsync(cancellationToken));
        }

        using (UserInfoUtil.Push(CreatePrincipal("user-b")))
        {
            Assert.Single(await context.ProjectConversations.ToListAsync(cancellationToken));
            Assert.Single(await context.ProjectConversationChatHistories.ToListAsync(cancellationToken));
            Assert.Single(await context.ProjectMemories.ToListAsync(cancellationToken));
            Assert.Single(await context.JobLogs.ToListAsync(cancellationToken));
            Assert.Single(await context.AgentflowNodeExecutionTraces.ToListAsync(cancellationToken));
            Assert.Single(await context.AgentflowNodes.ToListAsync(cancellationToken));
            Assert.Single(await context.AgentSkillRelations.ToListAsync(cancellationToken));
            Assert.Single(await context.ProjectSkillRelations.ToListAsync(cancellationToken));
        }

        using (UserInfoUtil.PushSystemScope())
        {
            Assert.Equal(2, await context.ProjectConversationChatHistories.CountAsync(cancellationToken));
            Assert.Equal(2, await context.ProjectMemories.CountAsync(cancellationToken));
            Assert.Equal(2, await context.JobLogs.CountAsync(cancellationToken));
            Assert.Equal(2, await context.AgentflowNodeExecutionTraces.CountAsync(cancellationToken));
            Assert.Equal(2, await context.AgentSkillRelations.CountAsync(cancellationToken));
            Assert.Equal(2, await context.ProjectSkillRelations.CountAsync(cancellationToken));
        }
    }

    [Fact]
    public async Task UserScopeFilter_AllRootsAndChildren_AreIsolatedByOwner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
        var now = DateTimeOffset.UtcNow;

        var projectA = CreateProject("same-name", "user-a");
        var projectB = CreateProject("same-name", "user-b");
        var conversationA = new ProjectConversation
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectA.Id,
            ContextId = "scope-a",
            Title = "conversation",
            CreateBy = "user-a",
            CreateTime = now,
        };
        var conversationB = new ProjectConversation
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectB.Id,
            ContextId = "scope-b",
            Title = "conversation",
            CreateBy = "user-b",
            CreateTime = now,
        };
        var jobA = CreateJob("same-job", "user-a");
        jobA.ProjectId = projectA.Id;
        var jobB = CreateJob("same-job", "user-b");
        jobB.ProjectId = projectB.Id;
        var providerA = new Provider
        {
            Id = Guid.CreateVersion7(),
            Name = "same-provider",
            ProviderType = ProviderType.OpenAIChatCompletions,
            Endpoint = "https://provider-a.test",
            CreateBy = "user-a",
            CreateTime = now,
        };
        var providerB = new Provider
        {
            Id = Guid.CreateVersion7(),
            Name = "same-provider",
            ProviderType = ProviderType.OpenAIChatCompletions,
            Endpoint = "https://provider-b.test",
            CreateBy = "user-b",
            CreateTime = now,
        };
        var modelA = CreateModel("same-model", "user-a", now);
        var modelB = CreateModel("same-model", "user-b", now);
        var modelProviderA = CreateModelProvider(modelA, providerA, "user-a", now);
        var modelProviderB = CreateModelProvider(modelB, providerB, "user-b", now);
        var agentA = CreateAgent("same-agent", "user-a");
        var agentB = CreateAgent("same-agent", "user-b");
        var flowA = CreateAgentflow("same-flow", "user-a");
        var flowB = CreateAgentflow("same-flow", "user-b");
        var flowNodeA = new AgentflowNode
        {
            AgentflowId = flowA.Id,
            NodeId = "node",
            Kind = AgentflowNodeKind.Agent,
        };
        var flowNodeB = new AgentflowNode
        {
            AgentflowId = flowB.Id,
            NodeId = "node",
            Kind = AgentflowNodeKind.Agent,
        };
        var flowEdgeA = new AgentflowEdge
        {
            AgentflowId = flowA.Id,
            EdgeId = "edge",
            SourceNodeId = flowNodeA.NodeId,
            TargetNodeId = flowNodeA.NodeId,
            SourceNode = flowNodeA,
            TargetNode = flowNodeA,
        };
        var flowEdgeB = new AgentflowEdge
        {
            AgentflowId = flowB.Id,
            EdgeId = "edge",
            SourceNodeId = flowNodeB.NodeId,
            TargetNodeId = flowNodeB.NodeId,
            SourceNode = flowNodeB,
            TargetNode = flowNodeB,
        };
        var mcpA = CreateMcpServer("same-mcp", "user-a", now);
        var mcpB = CreateMcpServer("same-mcp", "user-b", now);
        var skillA = CreateSkill("same-skill", "user-a");
        var skillB = CreateSkill("same-skill", "user-b");
        var builtInSkill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "shared-built-in",
            Description = "shared-built-in",
            Kind = SkillKind.BuiltIn,
            ContentPath = "skills/shared-built-in",
            CreateBy = Constants.AdminUserId,
        };
        var installationA = CreateInstallation("same-plugin", "user-a", now);
        var installationB = CreateInstallation("same-plugin", "user-b", now);
        var connectionA = CreateConnection("same-connection", "user-a", now);
        var connectionB = CreateConnection("same-connection", "user-b", now);
        var executionA = CreateDurableExecution("user-a", now);
        var executionB = CreateDurableExecution("user-b", now);

        await using (var seed = new AgwDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync(cancellationToken);
            seed.Providers.AddRange(providerA, providerB);
            seed.Models.AddRange(modelA, modelB);
            seed.ModelProviders.AddRange(modelProviderA, modelProviderB);
            seed.Agents.AddRange(agentA, agentB);
            seed.Agentflows.AddRange(flowA, flowB);
            seed.AgentflowNodes.AddRange(flowNodeA, flowNodeB);
            seed.AgentflowEdges.AddRange(flowEdgeA, flowEdgeB);
            seed.McpToolServers.AddRange(mcpA, mcpB);
            seed.Skills.AddRange(skillA, skillB, builtInSkill);
            seed.Projects.AddRange(projectA, projectB);
            seed.ProjectConversations.AddRange(conversationA, conversationB);
            seed.Jobs.AddRange(jobA, jobB);
            seed.PluginInstallations.AddRange(installationA, installationB);
            seed.Connections.AddRange(connectionA, connectionB);
            seed.ApiTokens.AddRange(
                CreateApiToken("user-a", "same-token", now),
                CreateApiToken("user-b", "same-token", now)
            );
            seed.UserMemories.AddRange(CreateUserMemory("user-a", now), CreateUserMemory("user-b", now));
            seed.AgentUsages.AddRange(
                CreateAgentUsage(projectA.Id, "user-a", now),
                CreateAgentUsage(projectB.Id, "user-b", now)
            );
            seed.DurableExecutions.AddRange(executionA, executionB);
            seed.AgentflowCheckpoints.AddRange(
                CreateCheckpoint(projectA, conversationA, flowA, "user-a", now),
                CreateCheckpoint(projectB, conversationB, flowB, "user-b", now)
            );
            seed.ProviderAuthConfigs.AddRange(
                new ProviderAuthConfig
                {
                    Id = Guid.CreateVersion7(),
                    ProviderId = providerA.Id,
                    ApiKey = "key-a",
                },
                new ProviderAuthConfig
                {
                    Id = Guid.CreateVersion7(),
                    ProviderId = providerB.Id,
                    ApiKey = "key-b",
                }
            );
            seed.RemoteSkillCaches.AddRange(
                new RemoteSkillCache
                {
                    SkillId = skillA.Id,
                    SourceUrl = "https://skills-a.test",
                    ContentJson = "{}",
                    FetchedAt = now,
                },
                new RemoteSkillCache
                {
                    SkillId = skillB.Id,
                    SourceUrl = "https://skills-b.test",
                    ContentJson = "{}",
                    FetchedAt = now,
                }
            );
            seed.AgentflowNodeExecutionTraces.AddRange(
                CreateTrace(projectA, flowA, "user-a", now),
                CreateTrace(projectB, flowB, "user-b", now)
            );
            seed.ProjectMemories.AddRange(
                CreateProjectMemory(projectA.Id, "user-a", now),
                CreateProjectMemory(projectB.Id, "user-b", now)
            );
            seed.ProjectConversationChatHistories.AddRange(
                CreateHistory(conversationA.Id, "user-a", now),
                CreateHistory(conversationB.Id, "user-b", now)
            );
            seed.ProjectConversationBindings.AddRange(
                CreateProjectConversationBinding(conversationA.Id, agentA.Id, "user-a", now),
                CreateProjectConversationBinding(conversationB.Id, agentB.Id, "user-b", now)
            );
            seed.JobLogs.AddRange(CreateJobLog(jobA.Id, "user-a", now), CreateJobLog(jobB.Id, "user-b", now));
            seed.PluginInstallationCredentials.AddRange(
                CreatePluginCredential(installationA.Id, "user-a", now),
                CreatePluginCredential(installationB.Id, "user-b", now)
            );
            seed.ConnectionCredentials.AddRange(
                CreateConnectionCredential(connectionA.Id, "user-a", now),
                CreateConnectionCredential(connectionB.Id, "user-b", now)
            );
            seed.AgentSessionStates.AddRange(
                new AgentSessionStateEntry
                {
                    ProjectConversationId = conversationA.Id,
                    AgentId = agentA.Id,
                    AgentflowNodeId = "node",
                    SerializedSession = "a",
                    UpdatedAt = now,
                },
                new AgentSessionStateEntry
                {
                    ProjectConversationId = conversationB.Id,
                    AgentId = agentB.Id,
                    AgentflowNodeId = "node",
                    SerializedSession = "b",
                    UpdatedAt = now,
                }
            );
            seed.AgentSkillRelations.AddRange(
                new AgentSkillRelation { AgentId = agentA.Id, SkillId = skillA.Id },
                new AgentSkillRelation { AgentId = agentB.Id, SkillId = skillB.Id },
                new AgentSkillRelation { AgentId = agentA.Id, SkillId = builtInSkill.Id },
                new AgentSkillRelation { AgentId = agentB.Id, SkillId = builtInSkill.Id }
            );
            seed.AgentMcpToolServers.AddRange(
                new AgentMcpServerRelation { AgentId = agentA.Id, McpToolServerId = mcpA.Id },
                new AgentMcpServerRelation { AgentId = agentB.Id, McpToolServerId = mcpB.Id }
            );
            seed.AgentConnectionRelations.AddRange(
                new AgentConnectionRelation { AgentId = agentA.Id, ConnectionId = connectionA.Id },
                new AgentConnectionRelation { AgentId = agentB.Id, ConnectionId = connectionB.Id }
            );
            seed.ProjectSkillRelations.AddRange(
                new ProjectSkillRelation { ProjectId = projectA.Id, SkillId = skillA.Id },
                new ProjectSkillRelation { ProjectId = projectB.Id, SkillId = skillB.Id },
                new ProjectSkillRelation { ProjectId = projectA.Id, SkillId = builtInSkill.Id },
                new ProjectSkillRelation { ProjectId = projectB.Id, SkillId = builtInSkill.Id }
            );
            seed.ProjectMcpToolServers.AddRange(
                new ProjectMcpServerRelation { ProjectId = projectA.Id, McpToolServerId = mcpA.Id },
                new ProjectMcpServerRelation { ProjectId = projectB.Id, McpToolServerId = mcpB.Id }
            );
            seed.ProjectConnectionRelations.AddRange(
                new ProjectConnectionRelation { ProjectId = projectA.Id, ConnectionId = connectionA.Id },
                new ProjectConnectionRelation { ProjectId = projectB.Id, ConnectionId = connectionB.Id }
            );
            seed.DurableExecutionEvents.AddRange(
                new DurableExecutionEventRecord
                {
                    Id = Guid.CreateVersion7(),
                    ExecutionId = executionA.Id,
                    SegmentIndex = 0,
                    Sequence = 0,
                    PayloadJson = "a",
                },
                new DurableExecutionEventRecord
                {
                    Id = Guid.CreateVersion7(),
                    ExecutionId = executionB.Id,
                    SegmentIndex = 0,
                    Sequence = 0,
                    PayloadJson = "b",
                }
            );
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var context = new AgwDbContext(options);
        using (UserInfoUtil.Push(CreatePrincipal("user-a")))
        {
            await AssertOwnerCountsAsync(context, expectedSkillCount: 2, cancellationToken);
            Assert.Equal(providerA.Id, Assert.Single(await context.Providers.ToListAsync(cancellationToken)).Id);
            Assert.Equal(connectionA.Id, Assert.Single(await context.Connections.ToListAsync(cancellationToken)).Id);
        }

        using (UserInfoUtil.Push(CreatePrincipal("user-b")))
        {
            await AssertOwnerCountsAsync(context, expectedSkillCount: 2, cancellationToken);
            Assert.Equal(providerB.Id, Assert.Single(await context.Providers.ToListAsync(cancellationToken)).Id);
            Assert.Equal(connectionB.Id, Assert.Single(await context.Connections.ToListAsync(cancellationToken)).Id);
        }

        using (UserInfoUtil.Push(null))
        {
            Assert.Empty(await context.Providers.ToListAsync(cancellationToken));
            Assert.Empty(await context.Connections.ToListAsync(cancellationToken));
            Assert.Empty(await context.ApiTokens.ToListAsync(cancellationToken));
        }
    }

    private static async Task AssertOwnerCountsAsync(
        AgwDbContext context,
        int expectedSkillCount,
        CancellationToken cancellationToken
    )
    {
        Assert.Equal(1, await context.Providers.CountAsync(cancellationToken));
        Assert.Equal(1, await context.ProviderAuthConfigs.CountAsync(cancellationToken));
        Assert.Equal(1, await context.Models.CountAsync(cancellationToken));
        Assert.Equal(1, await context.ModelProviders.CountAsync(cancellationToken));
        Assert.Equal(1, await context.Agents.CountAsync(cancellationToken));
        Assert.Equal(1, await context.Agentflows.CountAsync(cancellationToken));
        Assert.Equal(1, await context.McpToolServers.CountAsync(cancellationToken));
        Assert.Equal(expectedSkillCount, await context.Skills.CountAsync(cancellationToken));
        Assert.Equal(1, await context.Projects.CountAsync(cancellationToken));
        Assert.Equal(1, await context.ProjectConversations.CountAsync(cancellationToken));
        Assert.Equal(1, await context.Jobs.CountAsync(cancellationToken));
        Assert.Equal(1, await context.PluginInstallations.CountAsync(cancellationToken));
        Assert.Equal(1, await context.Connections.CountAsync(cancellationToken));
        Assert.Equal(1, await context.ApiTokens.CountAsync(cancellationToken));
        Assert.Equal(1, await context.UserMemories.CountAsync(cancellationToken));
        Assert.Equal(1, await context.AgentUsages.CountAsync(cancellationToken));
        Assert.Equal(1, await context.DurableExecutions.CountAsync(cancellationToken));
        Assert.Equal(1, await context.AgentflowCheckpoints.CountAsync(cancellationToken));
        Assert.Equal(1, await context.RemoteSkillCaches.CountAsync(cancellationToken));
        Assert.Equal(1, await context.AgentflowNodes.CountAsync(cancellationToken));
        Assert.Equal(1, await context.AgentflowEdges.CountAsync(cancellationToken));
        Assert.Equal(1, await context.AgentflowNodeExecutionTraces.CountAsync(cancellationToken));
        Assert.Equal(1, await context.ProjectMemories.CountAsync(cancellationToken));
        Assert.Equal(1, await context.ProjectConversationChatHistories.CountAsync(cancellationToken));
        Assert.Equal(1, await context.ProjectConversationBindings.CountAsync(cancellationToken));
        Assert.Equal(1, await context.JobLogs.CountAsync(cancellationToken));
        Assert.Equal(1, await context.PluginInstallationCredentials.CountAsync(cancellationToken));
        Assert.Equal(1, await context.ConnectionCredentials.CountAsync(cancellationToken));
        Assert.Equal(1, await context.AgentSessionStates.CountAsync(cancellationToken));
        Assert.Equal(2, await context.AgentSkillRelations.CountAsync(cancellationToken));
        Assert.Equal(1, await context.AgentMcpToolServers.CountAsync(cancellationToken));
        Assert.Equal(1, await context.AgentConnectionRelations.CountAsync(cancellationToken));
        Assert.Equal(2, await context.ProjectSkillRelations.CountAsync(cancellationToken));
        Assert.Equal(1, await context.ProjectMcpToolServers.CountAsync(cancellationToken));
        Assert.Equal(1, await context.ProjectConnectionRelations.CountAsync(cancellationToken));
        Assert.Equal(1, await context.DurableExecutionEvents.CountAsync(cancellationToken));
    }

    private static AgwAiModel CreateModel(string name, string owner, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            CreateBy = owner,
            CreateTime = now,
        };

    private static ModelProviderRelation CreateModelProvider(
        AgwAiModel model,
        Provider provider,
        string owner,
        DateTimeOffset now
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ModelId = model.Id,
            ProviderId = provider.Id,
            Model = model,
            Provider = provider,
            CreateBy = owner,
            CreateTime = now,
        };

    private static Agentflow CreateAgentflow(string name, string owner) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            CreateBy = owner,
        };

    private static McpServer CreateMcpServer(string name, string owner, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            CreateBy = owner,
            CreateTime = now,
        };

    private static PluginInstallation CreateInstallation(string pluginId, string owner, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            PluginId = pluginId,
            CreateBy = owner,
            CreateTime = now,
        };

    private static Connection CreateConnection(string alias, string owner, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            PluginId = "plugin",
            ConnectorId = "connector",
            AuthSchemeId = "auth",
            DisplayName = alias,
            Alias = alias,
            CreateBy = owner,
            CreateTime = now,
        };

    private static ApiToken CreateApiToken(string owner, string name, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            NormalizedName = ApiToken.NormalizeName(name),
            Prefix = "agw_test",
            SecretHash = new string('a', 64),
            CreateBy = owner,
            CreateTime = now,
        };

    private static UserMemory CreateUserMemory(string owner, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = owner,
            Name = "same-memory",
            NormalizedName = "SAME-MEMORY",
            Content = "memory",
            CreateTime = now,
        };

    private static AgentUsage CreateAgentUsage(Guid projectId, string owner, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            UserId = owner,
            ContextId = "usage-context",
            AgentName = "agent",
            RecordedAt = now,
        };

    private static DurableExecutionRecord CreateDurableExecution(string owner, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = owner,
            ManifestJson = "{}",
            StateChangedAt = now,
            StateVersion = Guid.CreateVersion7(),
        };

    private static AgentflowCheckpointRecord CreateCheckpoint(
        Project project,
        ProjectConversation conversation,
        Agentflow flow,
        string owner,
        DateTimeOffset now
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectId = project.Id,
            ProjectConversationId = conversation.Id,
            ContextId = conversation.ContextId,
            TaskId = Guid.CreateVersion7(),
            AgentflowId = flow.Id,
            UserId = owner,
            DefinitionFingerprint = "fingerprint",
            CheckpointJson = "{}",
            CreateBy = owner,
            CreateTime = now,
        };

    private static AgentflowTrace CreateTrace(Project project, Agentflow flow, string owner, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            StartTimeUtc = now,
            ProjectId = project.Id,
            ContextId = "trace-context",
            TaskId = Guid.CreateVersion7(),
            AgentflowId = flow.Id,
            NodeId = "node",
            Input = owner,
            Status = AgentflowNodeExecutionStatus.Succeeded,
        };

    private static ProjectMemoryEntry CreateProjectMemory(Guid projectId, string owner, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Path = $"{owner}.md",
            Content = owner,
            UpdatedAt = now,
        };

    private static ProjectConversationChatHistory CreateHistory(
        Guid conversationId,
        string owner,
        DateTimeOffset now
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            TaskId = Guid.CreateVersion7(),
            AgentName = owner,
            CreateTime = now,
        };

    private static ProjectConversationBinding CreateProjectConversationBinding(
        Guid conversationId,
        Guid agentId,
        string owner,
        DateTimeOffset now
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectConversationId = conversationId,
            AgentId = agentId,
            ExternalAgentName = $"external-{owner}",
            ProviderSessionId = $"session-{owner}",
            CreateBy = owner,
            CreateTime = now,
        };

    private static JobLog CreateJobLog(Guid jobId, string owner, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            JobId = jobId,
            TaskId = Guid.CreateVersion7(),
            StartTime = now,
            CreateBy = "scheduler",
            CreateTime = now,
        };

    private static PluginInstallationCredential CreatePluginCredential(
        Guid installationId,
        string owner,
        DateTimeOffset now
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            PluginInstallationId = installationId,
            Slot = $"secret-{owner}",
            Value = owner,
            CreateBy = owner,
            CreateTime = now,
        };

    private static ConnectionCredential CreateConnectionCredential(
        Guid connectionId,
        string owner,
        DateTimeOffset now
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ConnectionId = connectionId,
            Slot = $"secret-{owner}",
            Value = owner,
            CreateBy = owner,
            CreateTime = now,
        };

    private static ClaimsPrincipal CreatePrincipal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private static Project CreateProject(string name, string owner) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            CreateBy = owner,
            CreateTime = DateTimeOffset.UtcNow,
        };

    private static Job CreateJob(string name, string owner) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            Name = name,
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:01:00",
            NextRunTime = DateTimeOffset.UtcNow,
            CreateBy = owner,
            CreateTime = DateTimeOffset.UtcNow,
            UpdateBy = owner,
            UpdateTime = DateTimeOffset.UtcNow,
        };

    private static Agent CreateAgent(string name, string owner) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            DisplayName = name,
            CreateBy = owner,
        };

    private static Skill CreateSkill(string name, string owner) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Description = name,
            Kind = SkillKind.Local,
            ContentPath = $"skills/{name}",
            CreateBy = owner,
        };
}
