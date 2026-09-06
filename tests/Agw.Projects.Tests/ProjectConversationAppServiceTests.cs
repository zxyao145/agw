using System.Text.Json;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.Persistence;
using Agw.Shared;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Tests;

public class ProjectConversationAppServiceTests
{
    [Fact]
    public async Task ListResponsesAsync_GroupsTasksFromRecordsByContext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var contextId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var firstTaskId = Guid.CreateVersion7();
        var secondTaskId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.Add(CreateContext(contextId, projectId, "context-1", "Trip", jobId));
            seedContext.ProjectConversationChatHistories.AddRange(
                CreateRecord(contextId, firstTaskId, 0, "Tokyo trip", TaskExecutionStatus.Succeeded),
                CreateRecord(contextId, secondTaskId, 0, "Hotels", TaskExecutionStatus.Running)
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var contexts = await service.ListResponsesAsync(projectId);

        var context = Assert.Single(contexts);
        Assert.Equal(projectId.Normalize(), context.ProjectId);
        Assert.Equal(contextId, context.ConversationId);
        Assert.Equal("context-1", context.ContextId);
        Assert.Equal(jobId, context.JobId);
        Assert.Equal("Trip", context.Title);
        Assert.Equal(2, context.ExecutionCount);
        Assert.Equal(2, context.MessageCount);
        Assert.Equal(TaskExecutionStatus.Running, context.LatestStatus);
    }

    [Fact]
    public async Task ListResponsesAsync_InteractiveConversationWithoutMessages_IsIncluded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var interactiveContextId = Guid.CreateVersion7();
        var emptyJobContextId = Guid.CreateVersion7();
        var persistedJobContextId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.AddRange(
                CreateContext(interactiveContextId, projectId, "interactive-context", "New Chat"),
                CreateContext(emptyJobContextId, projectId, "empty-job-context", "Queued run", jobId),
                CreateContext(persistedJobContextId, projectId, "persisted-job-context", "Persisted", jobId)
            );
            seedContext.ProjectConversationChatHistories.AddRange(
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = interactiveContextId,
                    TaskId = Guid.CreateVersion7(),
                    Status = TaskExecutionStatus.Running,
                    CreateTime = TimeProvider.System.GetUtcNow(),
                    UpdateTime = TimeProvider.System.GetUtcNow(),
                },
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = emptyJobContextId,
                    TaskId = Guid.CreateVersion7(),
                    Status = TaskExecutionStatus.Running,
                    CreateTime = TimeProvider.System.GetUtcNow(),
                    UpdateTime = TimeProvider.System.GetUtcNow(),
                },
                CreateRecord(persistedJobContextId, Guid.CreateVersion7(), 0, "hello", TaskExecutionStatus.Running)
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var contexts = await service.ListResponsesAsync(projectId);

        Assert.Equal(2, contexts.Count);
        var contextsById = contexts.ToDictionary(context => context.ConversationId);
        var interactiveContext = contextsById[interactiveContextId];
        Assert.Equal("interactive-context", interactiveContext.ContextId);
        Assert.Equal(1, interactiveContext.ExecutionCount);
        Assert.Equal(0, interactiveContext.MessageCount);
        Assert.Contains(persistedJobContextId, contextsById.Keys);
        Assert.DoesNotContain(emptyJobContextId, contextsById.Keys);
    }

    [Fact]
    public async Task GetResponseAsync_ReturnsMetadataAndMessagePageForRequestedConversationOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var contextId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var otherContextId = Guid.CreateVersion7();
        var firstTaskId = Guid.CreateVersion7();
        var secondTaskId = Guid.CreateVersion7();
        var otherTaskId = Guid.CreateVersion7();
        var expectedUsage = new ProjectConversationUsage
        {
            InputTokenCount = 10,
            OutputTokenCount = 20,
            TotalTokenCount = 30,
            CachedInputTokenCount = 4,
            ReasoningTokenCount = 5,
        };
        await using (var seedContext = new AgwDbContext(options))
        {
            var requestedContext = CreateContext(contextId, projectId, "context-1", "Trip", jobId);
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.AddRange(
                requestedContext,
                CreateContext(otherContextId, projectId, "context-2", "Other")
            );
            seedContext.AgentUsages.AddRange(
                CreateUsage(projectId, "context-1", "planner", 8, 15, 23, 3, 2),
                CreateUsage(projectId, "context-1", "$summary", 2, 5, 7, 1, 3),
                CreateUsage(projectId, "context-2", "other", 100, 100, 200, 100, 100)
            );
            seedContext.ProjectConversationChatHistories.AddRange(
                CreateRecord(contextId, firstTaskId, 0, "Tokyo trip", TaskExecutionStatus.Succeeded),
                CreateRecord(contextId, secondTaskId, 1, "Hotels", TaskExecutionStatus.Succeeded),
                CreateRecord(otherContextId, otherTaskId, 0, "Wrong context", TaskExecutionStatus.Succeeded)
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var context = await service.GetResponseAsync(projectId, contextId, cancellationToken);

        Assert.NotNull(context);
        Assert.Equal(jobId, context.JobId);
        Assert.Equal(contextId, context.ConversationId);
        Assert.Equal("context-1", context.ContextId);
        Assert.Equal("Trip", context.Title);
        Assert.Equal(2, context.ExecutionCount);
        Assert.Equal(expectedUsage, context.Usage);

        var page = await service.GetMessagePageAsync(
            projectId,
            contextId,
            new ProjectConversationMessagesQuery(),
            cancellationToken
        );

        Assert.NotNull(page);
        Assert.Equal(["Tokyo trip", "Hotels"], page.Items.Select(GetMessageText));
    }

    [Fact]
    public async Task GetResponseAsync_ConversationIdNeverFallsBackToContextId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var contextId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.Add(
                CreateContext(conversationId, projectId, contextId.ToString("D"), "Distinct IDs")
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var response = await service.GetResponseAsync(projectId, conversationId, cancellationToken);
        var contextIdLookup = await service.GetResponseAsync(projectId, contextId, cancellationToken);

        Assert.NotNull(response);
        Assert.Equal(conversationId, response.ConversationId);
        Assert.Equal(contextId.ToString("D"), response.ContextId);
        Assert.Null(contextIdLookup);
    }

    [Fact]
    public async Task GetMessagePageAsync_WhenConversationContainsToolBlockState_ReturnsStateMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var contextId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var stateMessage = new ChatMessage(ChatRole.System, [new TextContent(string.Empty)])
        {
            MessageId = Guid.CreateVersion7().ToString(),
            AuthorName = "tools",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["type"] = ToolMessageTypes.TodoSnapshot,
                ["items"] = Array.Empty<object>(),
            },
        };
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.Add(CreateContext(contextId, projectId, "context-1", "Todo"));
            seedContext.ProjectConversationChatHistories.Add(
                CreateRecord(contextId, taskId, 0, stateMessage, TaskExecutionStatus.Succeeded)
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var page = await service.GetMessagePageAsync(
            projectId,
            contextId,
            new ProjectConversationMessagesQuery(),
            cancellationToken
        );

        var message = Assert.Single(page!.Items);
        Assert.Equal("tools", message.Author);
        Assert.Equal(ToolMessageTypes.TodoSnapshot, message.AdditionalProperties!["type"]?.ToString());
    }

    [Fact]
    public async Task GetResponseAsync_ReturnsResumeTargetAndAgentModeWithoutReturningMessages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var targetId = Guid.CreateVersion7();
        var targetRecord = CreateRecord(
            conversationId,
            Guid.CreateVersion7(),
            0,
            "hello",
            TaskExecutionStatus.Succeeded
        );
        targetRecord.Metadata = new Dictionary<string, JsonElement>
        {
            ["targetType"] = JsonSerializer.SerializeToElement("agent"),
            ["targetId"] = JsonSerializer.SerializeToElement(targetId.ToString("D")),
        };
        var modeRecord = CreateRecord(
            conversationId,
            Guid.CreateVersion7(),
            1,
            new ChatMessage(ChatRole.System, [new TextContent(string.Empty)])
            {
                MessageId = Guid.CreateVersion7().ToString(),
                AdditionalProperties = new AdditionalPropertiesDictionary { ["mode"] = "plan" },
            },
            TaskExecutionStatus.Succeeded
        );

        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.Add(CreateContext(conversationId, projectId, "conversation-1", "History"));
            seedContext.ProjectConversationChatHistories.AddRange(targetRecord, modeRecord);
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var response = await service.GetResponseAsync(projectId, conversationId, cancellationToken);

        Assert.NotNull(response);
        Assert.Equal("agent", response.ResumeState?.TargetType);
        Assert.Equal(targetId.ToString("D"), response.ResumeState?.TargetId);
        Assert.Equal("plan", response.ResumeState?.AgentMode);
        Assert.Null(typeof(ProjectConversationResponse).GetProperty("Messages"));
    }

    [Fact]
    public async Task GetMessagePageAsync_OrdersMessagesByConversationSequenceAcrossExecutions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var contextId = Guid.CreateVersion7();
        var firstTaskId = Guid.CreateVersion7();
        var secondTaskId = Guid.CreateVersion7();
        var now = TimeProvider.System.GetUtcNow();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.Add(CreateContext(contextId, projectId, "context-1", "Trip"));
            seedContext.ProjectConversationChatHistories.AddRange(
                CreateRecord(contextId, firstTaskId, 2, "third", TaskExecutionStatus.Succeeded, now),
                CreateRecord(contextId, secondTaskId, 0, "first", TaskExecutionStatus.Succeeded, now.AddSeconds(1)),
                CreateRecord(contextId, firstTaskId, 1, "second", TaskExecutionStatus.Succeeded, now.AddSeconds(2))
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var context = await service.GetResponseAsync(projectId, contextId, cancellationToken);
        var page = await service.GetMessagePageAsync(
            projectId,
            contextId,
            new ProjectConversationMessagesQuery(),
            cancellationToken
        );

        Assert.NotNull(context);
        Assert.Equal(2, context.ExecutionCount);
        Assert.Equal(new ProjectConversationUsage(), context.Usage);
        Assert.Equal(["first", "second", "third"], page!.Items.Select(GetMessageText));
    }

    [Fact]
    public async Task GetMessagePageAsync_NewerDirectionPaginatesWithoutOverlap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.Add(CreateContext(conversationId, projectId, "conversation-1", "History"));
            seedContext.ProjectConversationChatHistories.AddRange(
                Enumerable
                    .Range(0, 7)
                    .Select(index =>
                        CreateRecord(
                            conversationId,
                            Guid.CreateVersion7(),
                            index,
                            $"message-{index}",
                            TaskExecutionStatus.Succeeded
                        )
                    )
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);
        var first = await service.GetMessagePageAsync(
            projectId,
            conversationId,
            new ProjectConversationMessagesQuery
            {
                Direction = ProjectConversationMessageDirection.Newer,
                PageSize = 3,
            },
            cancellationToken
        );
        var second = await service.GetMessagePageAsync(
            projectId,
            conversationId,
            new ProjectConversationMessagesQuery
            {
                Direction = ProjectConversationMessageDirection.Newer,
                Cursor = first!.NextCursor,
                PageSize = 3,
            },
            cancellationToken
        );
        var third = await service.GetMessagePageAsync(
            projectId,
            conversationId,
            new ProjectConversationMessagesQuery
            {
                Direction = ProjectConversationMessageDirection.Newer,
                Cursor = second!.NextCursor,
                PageSize = 3,
            },
            cancellationToken
        );

        Assert.Equal(["message-0", "message-1", "message-2"], first.Items.Select(GetMessageText));
        Assert.Equal(["message-3", "message-4", "message-5"], second.Items.Select(GetMessageText));
        Assert.Equal(["message-6"], third!.Items.Select(GetMessageText));
        Assert.True(first.HasMore);
        Assert.True(second.HasMore);
        Assert.False(third.HasMore);
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public async Task GetMessagePageAsync_OlderDirectionStartsAtLatestAndRemainsStableAfterAppend()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.Add(CreateContext(conversationId, projectId, "conversation-1", "History"));
            seedContext.ProjectConversationChatHistories.AddRange(
                Enumerable
                    .Range(0, 6)
                    .Select(index =>
                        CreateRecord(
                            conversationId,
                            Guid.CreateVersion7(),
                            index,
                            $"message-{index}",
                            TaskExecutionStatus.Succeeded
                        )
                    )
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);
        var first = await service.GetMessagePageAsync(
            projectId,
            conversationId,
            new ProjectConversationMessagesQuery
            {
                Direction = ProjectConversationMessageDirection.Older,
                PageSize = 2,
            },
            cancellationToken
        );

        dbContext.ProjectConversationChatHistories.Add(
            CreateRecord(conversationId, Guid.CreateVersion7(), 6, "message-6", TaskExecutionStatus.Succeeded)
        );
        await dbContext.SaveChangesAsync(cancellationToken);

        var second = await service.GetMessagePageAsync(
            projectId,
            conversationId,
            new ProjectConversationMessagesQuery
            {
                Direction = ProjectConversationMessageDirection.Older,
                Cursor = first!.NextCursor,
                PageSize = 2,
            },
            cancellationToken
        );

        Assert.Equal(["message-4", "message-5"], first.Items.Select(GetMessageText));
        Assert.Equal(["message-2", "message-3"], second!.Items.Select(GetMessageText));
        Assert.DoesNotContain("message-6", second.Items.Select(GetMessageText));
        Assert.True(second.HasMore);
    }

    [Theory]
    [InlineData("not-a-cursor", 50)]
    [InlineData(null, 0)]
    [InlineData(null, 101)]
    public async Task GetMessagePageAsync_InvalidQueryThrowsAgwException(string? cursor, int pageSize)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("Data Source=:memory:")
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        await Assert.ThrowsAsync<AgwException>(() =>
            service.GetMessagePageAsync(
                Guid.CreateVersion7(),
                Guid.Empty,
                new ProjectConversationMessagesQuery { Cursor = cursor, PageSize = pageSize },
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task GetMessagePageAsync_UndefinedDirectionThrowsAgwException()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("Data Source=:memory:")
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        await Assert.ThrowsAsync<AgwException>(() =>
            service.GetMessagePageAsync(
                Guid.CreateVersion7(),
                Guid.Empty,
                new ProjectConversationMessagesQuery { Direction = (ProjectConversationMessageDirection)int.MaxValue },
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task DeleteAsync_RemovesContextAndRecords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var contextId = Guid.CreateVersion7();
        var otherContextId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.AddRange(
                CreateContext(contextId, projectId, "context-1", "Trip"),
                CreateContext(otherContextId, projectId, "context-2", "Other")
            );
            seedContext.AgentUsages.Add(CreateUsage(projectId, "context-1", "planner", 10, 20, 30, 4, 5));
            seedContext.ProjectConversationChatHistories.AddRange(
                CreateRecord(contextId, Guid.CreateVersion7(), 0, "Delete me", TaskExecutionStatus.Succeeded),
                CreateRecord(otherContextId, Guid.CreateVersion7(), 0, "Keep me", TaskExecutionStatus.Succeeded)
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var dbContext = new AgwDbContext(options))
        {
            var service = CreateService(dbContext);
            var deleted = await service.DeleteAsync(projectId, contextId);
            Assert.True(deleted);
        }

        await using var assertContext = new AgwDbContext(options);
        var remainingContext = Assert.Single(assertContext.ProjectConversations);
        Assert.Equal("context-2", remainingContext.ContextId);
        var remainingRecord = Assert.Single(assertContext.ProjectConversationChatHistories);
        Assert.Equal(otherContextId, remainingRecord.ConversationId);
        Assert.Single(assertContext.AgentUsages);
    }

    [Fact]
    public async Task DeleteAsync_DeletesContextSessionBindingsExplicitly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var contextId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.Add(CreateContext(contextId, projectId, "context-1", "Trip"));
            seedContext.TaskSessionBindings.Add(CreateBinding(contextId));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var deleted = await service.DeleteAsync(projectId, contextId);

        Assert.True(deleted);
        Assert.Empty(await dbContext.TaskSessionBindings.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task DeleteAllAsync_DeletesEachContextSessionBindingExplicitly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var firstContextId = Guid.CreateVersion7();
        var secondContextId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.AddRange(
                CreateContext(firstContextId, projectId, "context-1", "Trip"),
                CreateContext(secondContextId, projectId, "context-2", "Plan")
            );
            seedContext.TaskSessionBindings.AddRange(CreateBinding(firstContextId), CreateBinding(secondContextId));
            seedContext.AgentUsages.Add(CreateUsage(projectId, "context-1", "planner", 10, 20, 30, 4, 5));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var result = await service.DeleteAllAsync(projectId);

        Assert.Equal(ApplicationResultType.Success, result.Type);
        Assert.Empty(await dbContext.TaskSessionBindings.ToListAsync(cancellationToken));
        Assert.Single(await dbContext.AgentUsages.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task ClearRecordsAsync_RemovesContextSessionBindingsAndPreservesUsage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var contextId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            var projectConversation = CreateContext(contextId, projectId, "context-1", "Trip");
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.Add(projectConversation);
            seedContext.AgentUsages.Add(CreateUsage(projectId, "context-1", "planner", 10, 20, 30, 4, 5));
            seedContext.ProjectConversationChatHistories.Add(
                CreateRecord(contextId, Guid.CreateVersion7(), 0, "Clear me", TaskExecutionStatus.Succeeded)
            );
            seedContext.AgentflowCheckpoints.Add(
                new AgentflowCheckpointRecord
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = projectId,
                    ProjectConversationId = contextId,
                    ContextId = "context-1",
                    TaskId = Guid.CreateVersion7(),
                    AgentflowId = Guid.CreateVersion7(),
                    UserId = "tester-id",
                    BoundarySequence = 0,
                    DefinitionFingerprint = new string('a', 64),
                    MarkersJson = "[]",
                    CheckpointJson = "{}",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            seedContext.TaskSessionBindings.Add(
                new TaskSessionBinding
                {
                    Id = Guid.CreateVersion7(),
                    ProjectConversationId = contextId,
                    AgentId = Guid.CreateVersion7(),
                    ExternalAgentName = "codex",
                    ProviderSessionId = Guid.CreateVersion7().Normalize(),
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var result = await service.ClearRecordsAsync(projectId, contextId);

        Assert.Equal(ApplicationResultType.Success, result.Type);
        Assert.Empty(await dbContext.ProjectConversationChatHistories.ToListAsync(cancellationToken));
        Assert.Empty(await dbContext.AgentflowCheckpoints.ToListAsync(cancellationToken));
        Assert.Empty(await dbContext.TaskSessionBindings.ToListAsync(cancellationToken));
        Assert.Single(await dbContext.ProjectConversations.ToListAsync(cancellationToken));
        Assert.Single(await dbContext.AgentUsages.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task ClearRecordsAsync_AfterClearingContext_RemainsVisibleInList()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var contextId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.Add(CreateContext(contextId, projectId, "context-1", "Trip"));
            seedContext.ProjectConversationChatHistories.Add(
                CreateRecord(contextId, Guid.CreateVersion7(), 0, "Clear me", TaskExecutionStatus.Succeeded)
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var clearResult = await service.ClearRecordsAsync(projectId, contextId);
        var contexts = await service.ListResponsesAsync(projectId);

        Assert.Equal(ApplicationResultType.Success, clearResult.Type);
        var context = Assert.Single(contexts);
        Assert.Equal(contextId, context.ConversationId);
        Assert.Equal("context-1", context.ContextId);
        Assert.Equal(0, context.ExecutionCount);
        Assert.Equal(0, context.MessageCount);
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).UseSnakeCaseNamingConvention().Options;

    private static async Task EnsureCreatedAsync(
        DbContextOptions<AgwDbContext> options,
        CancellationToken cancellationToken
    )
    {
        await using var setupContext = new AgwDbContext(options);
        await setupContext.Database.EnsureCreatedAsync(cancellationToken);
    }

    private static Project CreateProject(Guid projectId, string name) =>
        new()
        {
            Id = projectId,
            Name = name,
            Type = ProjectType.UserDefined,
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static ProjectConversation CreateContext(
        Guid id,
        Guid projectId,
        string contextId,
        string title,
        Guid? jobId = null
    ) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            ContextId = contextId,
            JobId = jobId,
            Title = title,
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
            UpdateBy = "tester",
            UpdateTime = TimeProvider.System.GetUtcNow(),
        };

    private static AgentUsage CreateUsage(
        Guid projectId,
        string contextId,
        string agentName,
        long inputTokenCount,
        long outputTokenCount,
        long totalTokenCount,
        long cachedInputTokenCount,
        long reasoningTokenCount
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            ContextId = contextId,
            AgentName = agentName,
            UserId = "tester",
            RecordedAt = TimeProvider.System.GetUtcNow(),
            InputTokenCount = inputTokenCount,
            OutputTokenCount = outputTokenCount,
            TotalTokenCount = totalTokenCount,
            CachedInputTokenCount = cachedInputTokenCount,
            ReasoningTokenCount = reasoningTokenCount,
        };

    private static ProjectConversationChatHistory CreateRecord(
        Guid contextId,
        Guid taskId,
        long sequence,
        string text,
        TaskExecutionStatus status
    ) => CreateRecord(contextId, taskId, sequence, text, status, TimeProvider.System.GetUtcNow());

    private static ProjectConversationChatHistory CreateRecord(
        Guid contextId,
        Guid taskId,
        long sequence,
        string text,
        TaskExecutionStatus status,
        DateTimeOffset createTime
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ConversationId = contextId,
            TaskId = taskId,
            Status = status,
            ConversationSequence = sequence,
            ConversationPayload = JsonUtil.Serialize(
                new ChatMessage(ChatRole.User, text)
                {
                    MessageId = Guid.CreateVersion7().ToString(),
                    AuthorName = Constants.DefaultInputAuthor,
                }
            ),
            CreateTime = createTime,
            UpdateTime = createTime,
        };

    private static TaskSessionBinding CreateBinding(Guid conversationId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectConversationId = conversationId,
            AgentId = Guid.CreateVersion7(),
            ExternalAgentName = "codex",
            ProviderSessionId = Guid.CreateVersion7().Normalize(),
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static ProjectConversationChatHistory CreateRecord(
        Guid contextId,
        Guid taskId,
        long sequence,
        ChatMessage message,
        TaskExecutionStatus status
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ConversationId = contextId,
            TaskId = taskId,
            Status = status,
            ConversationSequence = sequence,
            ConversationPayload = JsonUtil.Serialize(message),
            CreateTime = TimeProvider.System.GetUtcNow(),
            UpdateTime = TimeProvider.System.GetUtcNow(),
        };

    private static string? GetMessageText(AgwMessage message) => (message.Contents[0] as AgwTextContent)?.Content;

    private static ProjectConversationAppService CreateService(
        AgwDbContext dbContext,
        IProjectDeletionCoordinator? deletionCoordinator = null
    )
    {
        var userInfo = new TestUserInfoService();

        return new ProjectConversationAppService(
            dbContext,
            new ProjectResolver(dbContext, userInfo),
            deletionCoordinator ?? TestProjectPersistence.CreateDeletionCoordinator(dbContext),
            TimeProvider.System
        );
    }
}
