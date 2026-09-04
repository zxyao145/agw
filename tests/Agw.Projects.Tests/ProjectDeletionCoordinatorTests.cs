using System.Security.Claims;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public sealed class ProjectDeletionCoordinatorTests
{
    [Fact]
    public async Task DeleteProjectAsync_WithoutPhysicalForeignKeys_RemovesDependentsAndPreservesUsage()
    {
        // Arrange
        using var userScope = PushUser("tester");
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var otherProjectId = Guid.CreateVersion7();
        var otherConversationId = Guid.CreateVersion7();
        var jobId = await SeedProjectGraphAsync(options, projectId, conversationId, "tester", "context-1");
        var otherJobId = await SeedProjectGraphAsync(
            options,
            otherProjectId,
            otherConversationId,
            "other-user",
            "context-2"
        );
        await using var dbContext = new AgwDbContext(options);
        var coordinator = new ProjectDeletionCoordinator(dbContext);

        // Act
        var deleted = await coordinator.DeleteProjectAsync(
            new ProjectDeletionTarget(projectId, "tester"),
            cancellationToken
        );

        // Assert
        Assert.True(deleted);
        using var systemScope = UserInfoUtil.PushSystemScope();
        await using var assertContext = new AgwDbContext(options);
        Assert.DoesNotContain(
            await assertContext.Projects.ToListAsync(cancellationToken),
            project => project.Id == projectId
        );
        Assert.Contains(
            await assertContext.Projects.ToListAsync(cancellationToken),
            project => project.Id == otherProjectId
        );
        Assert.DoesNotContain(
            await assertContext.ProjectConversations.ToListAsync(cancellationToken),
            conversation => conversation.ProjectId == projectId
        );
        Assert.DoesNotContain(
            await assertContext.ProjectConversationChatHistories.ToListAsync(cancellationToken),
            history => history.ConversationId == conversationId
        );
        Assert.DoesNotContain(
            await assertContext.TaskSessionBindings.ToListAsync(cancellationToken),
            binding => binding.ProjectConversationId == conversationId
        );
        Assert.DoesNotContain(
            await assertContext.AgentSessionStates.ToListAsync(cancellationToken),
            session => session.ProjectConversationId == conversationId
        );
        Assert.DoesNotContain(
            await assertContext.AgentflowCheckpoints.ToListAsync(cancellationToken),
            checkpoint => checkpoint.ProjectId == projectId
        );
        Assert.DoesNotContain(
            await assertContext.AgentflowNodeExecutionTraces.ToListAsync(cancellationToken),
            trace => trace.ProjectId == projectId
        );
        Assert.DoesNotContain(
            await assertContext.ProjectSkillRelations.ToListAsync(cancellationToken),
            relation => relation.ProjectId == projectId
        );
        Assert.DoesNotContain(
            await assertContext.ProjectMcpToolServers.ToListAsync(cancellationToken),
            relation => relation.ProjectId == projectId
        );
        Assert.DoesNotContain(
            await assertContext.ProjectConnectionRelations.ToListAsync(cancellationToken),
            relation => relation.ProjectId == projectId
        );
        Assert.Contains(
            await assertContext.AgentUsages.ToListAsync(cancellationToken),
            usage => usage.ProjectId == projectId
        );
        Assert.DoesNotContain(
            await assertContext.ProjectMemories.ToListAsync(cancellationToken),
            memory => memory.ProjectId == projectId
        );
        Assert.Contains(
            await assertContext.ProjectMemories.ToListAsync(cancellationToken),
            memory => memory.ProjectId == otherProjectId
        );
        Assert.DoesNotContain(
            await assertContext.Jobs.ToListAsync(cancellationToken),
            job => job.ProjectId == projectId
        );
        Assert.DoesNotContain(await assertContext.JobLogs.ToListAsync(cancellationToken), log => log.JobId == jobId);
        Assert.Contains(
            await assertContext.Jobs.ToListAsync(cancellationToken),
            job => job.ProjectId == otherProjectId
        );
        Assert.Contains(await assertContext.JobLogs.ToListAsync(cancellationToken), log => log.JobId == otherJobId);
        Assert.Contains(
            await assertContext.ProjectConversations.ToListAsync(cancellationToken),
            conversation => conversation.Id == otherConversationId
        );
    }

    [Fact]
    public async Task DeleteProjectAsync_ProjectDeleteFails_RollsBackDependentDeletes()
    {
        // Arrange
        using var userScope = PushUser("tester");
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await SeedProjectGraphAsync(options, projectId, conversationId, "tester", "context-1");
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER reject_project_delete
            BEFORE DELETE ON project
            BEGIN
                SELECT RAISE(ABORT, 'project delete rejected');
            END;
            """,
            cancellationToken
        );
        var coordinator = new ProjectDeletionCoordinator(dbContext);

        // Act
        await Assert.ThrowsAsync<SqliteException>(() =>
            coordinator.DeleteProjectAsync(new ProjectDeletionTarget(projectId, "tester"), cancellationToken)
        );

        // Assert
        using var systemScope = UserInfoUtil.PushSystemScope();
        await using var assertContext = new AgwDbContext(options);
        Assert.Contains(
            await assertContext.Projects.ToListAsync(cancellationToken),
            project => project.Id == projectId
        );
        Assert.Contains(
            await assertContext.ProjectConversations.ToListAsync(cancellationToken),
            conversation => conversation.Id == conversationId
        );
        Assert.Contains(
            await assertContext.ProjectConversationChatHistories.ToListAsync(cancellationToken),
            history => history.ConversationId == conversationId
        );
        Assert.Contains(
            await assertContext.AgentflowCheckpoints.ToListAsync(cancellationToken),
            checkpoint => checkpoint.ProjectId == projectId
        );
        Assert.Contains(
            await assertContext.AgentflowNodeExecutionTraces.ToListAsync(cancellationToken),
            trace => trace.ProjectId == projectId
        );
    }

    [Fact]
    public async Task DeleteProjectAsync_RunningJobExists_ThrowsConflictAndRollsBack()
    {
        // Arrange
        using var userScope = PushUser("tester");
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var jobId = await SeedProjectGraphAsync(options, projectId, conversationId, "tester", "context-1");
        await using (var seedContext = new AgwDbContext(options))
        {
            var now = TimeProvider.System.GetUtcNow();
            seedContext.Jobs.Add(
                new Job
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = projectId,
                    Name = "running-job",
                    TriggerType = TriggerType.Interval,
                    TriggerValue = "60",
                    NextRunTime = now,
                    Status = JobStatus.Running,
                    ActiveExecutionId = Guid.CreateVersion7(),
                    ActiveAttemptStartedAt = now,
                    CreateBy = "tester",
                    CreateTime = now,
                }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }
        await using var dbContext = new AgwDbContext(options);
        var coordinator = new ProjectDeletionCoordinator(dbContext);

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            coordinator.DeleteProjectAsync(new ProjectDeletionTarget(projectId, "tester"), cancellationToken)
        );

        // Assert
        Assert.Equal(ErrorCodes.JobActiveAttemptConflict.Code, exception.Code);
        using var systemScope = UserInfoUtil.PushSystemScope();
        await using var assertContext = new AgwDbContext(options);
        Assert.Contains(
            await assertContext.Projects.ToListAsync(cancellationToken),
            project => project.Id == projectId
        );
        Assert.Contains(
            await assertContext.ProjectConversations.ToListAsync(cancellationToken),
            conversation => conversation.Id == conversationId
        );
        Assert.Equal(2, await assertContext.Jobs.CountAsync(cancellationToken));
        Assert.Contains(await assertContext.Jobs.ToListAsync(cancellationToken), job => job.Id == jobId);
    }

    [Fact]
    public async Task DeleteProjectAsync_JobDeleteLosesRace_ThrowsConflictAndRollsBack()
    {
        // Arrange
        using var userScope = PushUser("tester");
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var jobId = await SeedProjectGraphAsync(options, projectId, conversationId, "tester", "context-1");
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER skip_job_delete
            BEFORE DELETE ON job
            BEGIN
                SELECT RAISE(IGNORE);
            END;
            """,
            cancellationToken
        );
        var coordinator = new ProjectDeletionCoordinator(dbContext);

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            coordinator.DeleteProjectAsync(new ProjectDeletionTarget(projectId, "tester"), cancellationToken)
        );

        // Assert
        Assert.Equal(ErrorCodes.JobActiveAttemptConflict.Code, exception.Code);
        using var systemScope = UserInfoUtil.PushSystemScope();
        await using var assertContext = new AgwDbContext(options);
        Assert.Contains(
            await assertContext.Projects.ToListAsync(cancellationToken),
            project => project.Id == projectId
        );
        Assert.Contains(await assertContext.Jobs.ToListAsync(cancellationToken), job => job.Id == jobId);
        Assert.Contains(await assertContext.JobLogs.ToListAsync(cancellationToken), log => log.JobId == jobId);
    }

    private static async Task<Guid> SeedProjectGraphAsync(
        DbContextOptions<AgwDbContext> options,
        Guid projectId,
        Guid conversationId,
        string ownerUserId,
        string contextId
    )
    {
        await using var context = new AgwDbContext(options);
        var now = TimeProvider.System.GetUtcNow();
        context.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = $"Project-{projectId:N}",
                CreateBy = ownerUserId,
                CreateTime = now,
            }
        );
        context.ProjectConversations.Add(
            new ProjectConversation
            {
                Id = conversationId,
                ProjectId = projectId,
                ContextId = contextId,
                Title = contextId,
                CreateBy = ownerUserId,
                CreateTime = now,
            }
        );
        context.ProjectConversationChatHistories.Add(
            new ProjectConversationChatHistory
            {
                Id = Guid.CreateVersion7(),
                ConversationId = conversationId,
                TaskId = Guid.CreateVersion7(),
                ConversationSequence = 0,
                ConversationPayload = "{}",
                CreateTime = now,
            }
        );
        context.TaskSessionBindings.Add(
            new TaskSessionBinding
            {
                Id = Guid.CreateVersion7(),
                ProjectConversationId = conversationId,
                AgentId = Guid.CreateVersion7(),
                ExternalAgentName = "codex",
                ProviderSessionId = Guid.CreateVersion7().ToString("D"),
                CreateBy = ownerUserId,
                CreateTime = now,
            }
        );
        context.AgentSessionStates.Add(
            new AgentSessionStateEntry
            {
                ProjectConversationId = conversationId,
                AgentId = Guid.CreateVersion7(),
                SerializedSession = "{}",
                UpdatedAt = now,
            }
        );
        context.AgentflowCheckpoints.Add(
            new AgentflowCheckpointRecord
            {
                Id = Guid.CreateVersion7(),
                ProjectId = projectId,
                ProjectConversationId = conversationId,
                ContextId = contextId,
                TaskId = Guid.CreateVersion7(),
                AgentflowId = Guid.CreateVersion7(),
                UserId = ownerUserId,
                BoundarySequence = 0,
                DefinitionFingerprint = new string('a', 64),
                MarkersJson = "[]",
                CheckpointJson = "{}",
                CreateBy = ownerUserId,
                CreateTime = now,
            }
        );
        context.AgentflowNodeExecutionTraces.Add(
            new AgentflowTrace
            {
                Id = Guid.CreateVersion7(),
                ProjectId = projectId,
                ContextId = contextId,
                TaskId = Guid.CreateVersion7(),
                AgentflowId = Guid.CreateVersion7(),
                NodeId = "node-1",
                NodeKind = AgentflowNodeKind.Agent,
                Input = "input",
                Status = AgentflowNodeExecutionStatus.Succeeded,
                StartTimeUtc = now,
            }
        );
        context.ProjectSkillRelations.Add(
            new ProjectSkillRelation { ProjectId = projectId, SkillId = Guid.CreateVersion7() }
        );
        context.ProjectMcpToolServers.Add(
            new ProjectMcpServerRelation { ProjectId = projectId, McpToolServerId = Guid.CreateVersion7() }
        );
        context.ProjectConnectionRelations.Add(
            new ProjectConnectionRelation { ProjectId = projectId, ConnectionId = Guid.CreateVersion7() }
        );
        context.ProjectMemories.Add(
            new ProjectMemoryEntry
            {
                Id = Guid.CreateVersion7(),
                ProjectId = projectId,
                Path = "memory/index.md",
                Content = "project memory",
                UpdatedAt = now,
            }
        );
        var jobId = Guid.CreateVersion7();
        context.Jobs.Add(
            new Job
            {
                Id = jobId,
                ProjectId = projectId,
                Name = $"Job-{projectId:N}",
                TriggerType = TriggerType.Interval,
                TriggerValue = "3600",
                NextRunTime = now,
                CreateBy = ownerUserId,
                CreateTime = now,
            }
        );
        context.JobLogs.Add(
            new JobLog
            {
                Id = Guid.CreateVersion7(),
                JobId = jobId,
                TaskId = Guid.CreateVersion7(),
                StartTime = now,
                Success = true,
                Attempt = 1,
                CreateBy = ownerUserId,
                CreateTime = now,
            }
        );
        context.AgentUsages.Add(
            new AgentUsage
            {
                Id = Guid.CreateVersion7(),
                ProjectId = projectId,
                UserId = ownerUserId,
                ContextId = contextId,
                AgentName = "planner",
                RecordedAt = now,
            }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return jobId;
    }

    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).UseSnakeCaseNamingConvention().Options;

    private static async Task EnsureCreatedAsync(DbContextOptions<AgwDbContext> options)
    {
        await using var context = new AgwDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    private static IDisposable PushUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], authenticationType: "Test")
            )
        );
}
