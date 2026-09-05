using System.Security.Claims;
using System.Text.Json;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
        var executionId = await SeedDurableExecutionAsync(options, projectId, conversationId, "tester");
        var otherJobId = await SeedProjectGraphAsync(
            options,
            otherProjectId,
            otherConversationId,
            "other-user",
            "context-2"
        );
        var otherExecutionId = await SeedDurableExecutionAsync(
            options,
            otherProjectId,
            otherConversationId,
            "other-user"
        );
        await using var dbContext = new AgwDbContext(options);
        var coordinator = TestProjectPersistence.CreateDeletionCoordinator(dbContext);

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
        Assert.DoesNotContain(
            await assertContext.DurableExecutions.ToListAsync(cancellationToken),
            execution => execution.Id == executionId
        );
        Assert.DoesNotContain(
            await assertContext.DurableExecutionEvents.ToListAsync(cancellationToken),
            entry => entry.ExecutionId == executionId
        );
        Assert.Contains(
            await assertContext.DurableExecutions.ToListAsync(cancellationToken),
            execution => execution.Id == otherExecutionId
        );
        Assert.Contains(
            await assertContext.DurableExecutionEvents.ToListAsync(cancellationToken),
            entry => entry.ExecutionId == otherExecutionId
        );
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
        var coordinator = TestProjectPersistence.CreateDeletionCoordinator(dbContext);

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
    public async Task DeleteProjectAsync_PendingBusyExecution_DoesNotDeleteProject()
    {
        // Arrange
        using var userScope = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await SeedProjectGraphAsync(options, projectId, conversationId, "tester", "context-1");
        var executionId = await SeedDurableExecutionAsync(options, projectId, conversationId, "tester", "broken");
        await using var context = new AgwDbContext(options);
        var coordinator = TestProjectPersistence.CreateDeletionCoordinator(context);
        await using var lease = await InMemoryApplicationLock.Shared.AcquireAsync(
            DurableExecutionLock.GetResourceName(executionId),
            token
        );

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            coordinator.DeleteProjectAsync(new ProjectDeletionTarget(projectId, "tester"), token)
        );

        // Assert
        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, exception.Code);
        Assert.True(await context.Projects.AnyAsync(row => row.Id == projectId, token));
        Assert.True(await context.ProjectConversations.AnyAsync(row => row.Id == conversationId, token));
        Assert.False(
            await context
                .DurableExecutions.Where(row => row.Id == executionId)
                .Select(row => row.ScopeBackfilled)
                .SingleAsync(token)
        );
    }

    [Fact]
    public async Task DeleteProjectAsync_QuarantinedWrongIndex_PreservesExecutionAndEvents()
    {
        // Arrange
        using var userScope = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var wrongProjectId = Guid.CreateVersion7();
        await SeedProjectGraphAsync(options, projectId, conversationId, "tester", "actual");
        await SeedProjectGraphAsync(options, wrongProjectId, Guid.CreateVersion7(), "tester", "wrong");
        var executionId = await SeedDurableExecutionAsync(options, projectId, conversationId, "tester");
        await using var context = new AgwDbContext(options);
        await context
            .DurableExecutions.Where(row => row.Id == executionId)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(row => row.ProjectId, wrongProjectId)
                        .SetProperty(row => row.ProjectConversationId, conversationId)
                        .SetProperty(row => row.ScopeBackfilled, true),
                token
            );
        var maintenance = new DurableExecutionScopeMaintenance(
            context,
            InMemoryApplicationLock.Shared,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DurableExecutionScopeMaintenance>.Instance
        );
        await using (
            var lease = await InMemoryApplicationLock.Shared.AcquireAsync(
                DurableExecutionLock.GetResourceName(executionId),
                token
            )
        )
        {
            Assert.False(await maintenance.ValidateLockedExecutionAsync(executionId, token));
        }
        var coordinator = new ProjectDeletionCoordinator(context, InMemoryApplicationLock.Shared, maintenance);

        // Act
        var deleted = await coordinator.DeleteProjectAsync(new ProjectDeletionTarget(wrongProjectId, "tester"), token);

        // Assert
        Assert.True(deleted);
        var retained = await context
            .DurableExecutions.Where(row => row.Id == executionId)
            .Select(row => new
            {
                row.ProjectId,
                row.ProjectConversationId,
                row.Status,
            })
            .SingleAsync(token);
        Assert.Null(retained.ProjectId);
        Assert.Null(retained.ProjectConversationId);
        Assert.Equal(DurableExecutionStatus.Failed, retained.Status);
        Assert.True(await context.DurableExecutionEvents.AnyAsync(row => row.ExecutionId == executionId, token));
        Assert.True(await context.Projects.AnyAsync(row => row.Id == projectId, token));
    }

    [Fact]
    public async Task DeleteConversationAsync_DurableExecutionForConversation_RemovesExecutionAndStreamEntries()
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
        var executionId = await SeedDurableExecutionAsync(options, projectId, conversationId, "tester");
        var otherConversationId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = otherConversationId,
                    ProjectId = projectId,
                    ContextId = "context-2",
                    Title = "context-2",
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }
        var otherExecutionId = await SeedDurableExecutionAsync(options, projectId, otherConversationId, "tester");
        await using var dbContext = new AgwDbContext(options);
        var coordinator = TestProjectPersistence.CreateDeletionCoordinator(dbContext);

        // Act
        var deleted = await coordinator.DeleteConversationAsync(
            new ProjectConversationDeletionTarget(projectId, conversationId, "context-1", "tester"),
            cancellationToken
        );

        // Assert
        Assert.True(deleted);
        using var systemScope = UserInfoUtil.PushSystemScope();
        await using var assertContext = new AgwDbContext(options);
        Assert.DoesNotContain(
            await assertContext.DurableExecutions.ToListAsync(cancellationToken),
            execution => execution.Id == executionId
        );
        Assert.DoesNotContain(
            await assertContext.DurableExecutionEvents.ToListAsync(cancellationToken),
            entry => entry.ExecutionId == executionId
        );
        Assert.Contains(
            await assertContext.DurableExecutions.ToListAsync(cancellationToken),
            execution => execution.Id == otherExecutionId
        );
        Assert.Contains(
            await assertContext.DurableExecutionEvents.ToListAsync(cancellationToken),
            entry => entry.ExecutionId == otherExecutionId
        );
        Assert.Contains(
            await assertContext.Projects.ToListAsync(cancellationToken),
            project => project.Id == projectId
        );
    }

    [Fact]
    public async Task DeleteProjectAsync_InvalidDurableManifest_LogsExecutionIdWithoutManifestContent()
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
        const string invalidManifest = "sensitive-invalid-manifest";
        var executionId = await SeedDurableExecutionAsync(
            options,
            projectId,
            conversationId,
            "tester",
            invalidManifest
        );
        await using var dbContext = new AgwDbContext(options);
        var logger = new ListLogger<DurableExecutionScopeMaintenance>();
        var coordinator = new ProjectDeletionCoordinator(
            dbContext,
            InMemoryApplicationLock.Shared,
            scopeMaintenance: new DurableExecutionScopeMaintenance(
                dbContext,
                InMemoryApplicationLock.Shared,
                TimeProvider.System,
                logger
            )
        );

        // Act
        var deleted = await coordinator.DeleteProjectAsync(
            new ProjectDeletionTarget(projectId, "tester"),
            cancellationToken
        );

        // Assert
        Assert.True(deleted);
        var warning = Assert.Single(logger.Messages);
        Assert.Contains(executionId.ToString(), warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(invalidManifest, warning, StringComparison.Ordinal);
        using var systemScope = UserInfoUtil.PushSystemScope();
        await using var assertContext = new AgwDbContext(options);
        Assert.Contains(
            await assertContext.DurableExecutions.ToListAsync(cancellationToken),
            execution => execution.Id == executionId
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
        var coordinator = TestProjectPersistence.CreateDeletionCoordinator(dbContext);

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
        var coordinator = TestProjectPersistence.CreateDeletionCoordinator(dbContext);

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

    [Fact]
    public async Task DeleteProjectAsync_IndexedExecutionWithCorruptCiphertext_DeletesWithoutReadingManifest()
    {
        // Arrange
        using var userScope = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await SeedProjectGraphAsync(options, projectId, conversationId, "tester", "context-1");
        var executionId = await SeedDurableExecutionAsync(options, projectId, conversationId, "tester");
        var unrelatedProjectId = Guid.CreateVersion7();
        var unrelatedConversationId = Guid.CreateVersion7();
        var unrelated = await SeedDurableExecutionAsync(options, unrelatedProjectId, unrelatedConversationId, "tester");
        await using var dbContext = new AgwDbContext(options);
        foreach (
            var row in new[]
            {
                (Id: executionId, Project: projectId, Conversation: conversationId),
                (Id: unrelated, Project: unrelatedProjectId, Conversation: unrelatedConversationId),
            }
        )
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE durable_execution SET project_id = {row.Project}, project_conversation_id = {row.Conversation}, scope_backfilled = 1, manifest_json = 'agwenc:v1:corrupt' WHERE id = {row.Id}",
                token
            );
        }

        // Act
        var deleted = await TestProjectPersistence
            .CreateDeletionCoordinator(dbContext)
            .DeleteProjectAsync(new ProjectDeletionTarget(projectId, "tester"), token);

        // Assert
        Assert.True(deleted);
        using var system = UserInfoUtil.PushSystemScope();
        Assert.Equal(new[] { unrelated }, await dbContext.DurableExecutions.Select(row => row.Id).ToArrayAsync(token));
        Assert.Equal(
            new[] { unrelated },
            await dbContext.DurableExecutionEvents.Select(row => row.ExecutionId).ToArrayAsync(token)
        );
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

    private static async Task<Guid> SeedDurableExecutionAsync(
        DbContextOptions<AgwDbContext> options,
        Guid projectId,
        Guid conversationId,
        string ownerUserId,
        string? manifestJson = null
    )
    {
        await using var context = new AgwDbContext(options);
        var executionId = Guid.CreateVersion7();
        var now = TimeProvider.System.GetUtcNow();
        context.DurableExecutions.Add(
            new DurableExecutionRecord
            {
                Id = executionId,
                UserId = ownerUserId,
                ManifestJson =
                    manifestJson
                    ?? JsonSerializer.Serialize(
                        new
                        {
                            schemaVersion = 1,
                            executionId,
                            userId = ownerUserId,
                            agentId = Guid.CreateVersion7(),
                            agentType = 0,
                            input = new { contents = Array.Empty<object>() },
                            settings = new { environmentVariables = new { }, resume = false },
                            task = new
                            {
                                taskId = Guid.CreateVersion7(),
                                projectId,
                                projectConversationId = conversationId,
                                contextId = "context-1",
                            },
                        }
                    ),
                Status = DurableExecutionStatus.Queued,
                StateChangedAt = now,
                StateVersion = Guid.CreateVersion7(),
                CreateBy = ownerUserId,
                CreateTime = now,
                UpdateBy = ownerUserId,
                UpdateTime = now,
            }
        );
        context.DurableExecutionEvents.Add(
            new DurableExecutionEventRecord
            {
                Id = Guid.CreateVersion7(),
                ExecutionId = executionId,
                SegmentIndex = 0,
                Sequence = 0,
                PayloadJson = "{}",
            }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return executionId;
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

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
