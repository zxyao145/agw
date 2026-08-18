using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Projects.Application;
using Agw.Projects.Domain.Services;
using Agw.Shared;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Tests;

public class ProjectContextAppServiceTests
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
        Assert.Equal("context-1", context.ContextId);
        Assert.Equal(jobId, context.JobId);
        Assert.Equal("Trip", context.Title);
        Assert.Equal(2, context.ExecutionCount);
        Assert.Equal(2, context.MessageCount);
        Assert.Equal(TaskExecutionStatus.Running, context.LatestStatus);
    }

    [Fact]
    public async Task ListResponsesAsync_SkipsContextsWithExecutionsButWithoutMessages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var transientContextId = Guid.CreateVersion7();
        var emptyTitledContextId = Guid.CreateVersion7();
        var persistedContextId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.AddRange(
                CreateContext(transientContextId, projectId, "pending-context", "New Chat"),
                CreateContext(emptyTitledContextId, projectId, "empty-titled-context", "Queued run"),
                CreateContext(persistedContextId, projectId, "persisted-context", "Persisted")
            );
            seedContext.ProjectConversationChatHistories.AddRange(
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = transientContextId,
                    TaskId = Guid.CreateVersion7(),
                    Status = TaskExecutionStatus.Running,
                    CreateTime = TimeProvider.System.GetUtcNow(),
                    UpdateTime = TimeProvider.System.GetUtcNow(),
                },
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = emptyTitledContextId,
                    TaskId = Guid.CreateVersion7(),
                    Status = TaskExecutionStatus.Running,
                    CreateTime = TimeProvider.System.GetUtcNow(),
                    UpdateTime = TimeProvider.System.GetUtcNow(),
                },
                CreateRecord(persistedContextId, Guid.CreateVersion7(), 0, "hello", TaskExecutionStatus.Running)
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var contexts = await service.ListResponsesAsync(projectId);

        var context = Assert.Single(contexts);
        Assert.Equal("persisted-context", context.ContextId);
    }

    [Fact]
    public async Task GetResponseAsync_ReturnsMessagesForRequestedContextOnly()
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
        var expectedUsage = new ProjectContextUsage
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
                CreateRecord(contextId, secondTaskId, 0, "Hotels", TaskExecutionStatus.Succeeded),
                CreateRecord(otherContextId, otherTaskId, 0, "Wrong context", TaskExecutionStatus.Succeeded)
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var context = await service.GetResponseAsync(projectId, "context-1");

        Assert.NotNull(context);
        Assert.Equal(jobId, context.JobId);
        Assert.Equal(2, context.ExecutionCount);
        Assert.Equal(expectedUsage, context.Usage);
        Assert.Equal(["Tokyo trip", "Hotels"], context.Messages!.Select(GetMessageText));
    }

    [Fact]
    public async Task GetResponseAsync_WhenContextContainsToolBlockState_ReturnsStateMessage()
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

        var context = await service.GetResponseAsync(projectId, "context-1");

        var message = Assert.Single(context!.Messages!);
        Assert.Equal("tools", message.Author);
        Assert.Equal(ToolMessageTypes.TodoSnapshot, message.AdditionalProperties!["type"]?.ToString());
    }

    [Fact]
    public async Task GetResponseAsync_OrdersMessagesByContextConversationSequenceAcrossExecutions()
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

        var context = await service.GetResponseAsync(projectId, "context-1");

        Assert.NotNull(context);
        Assert.Equal(2, context.ExecutionCount);
        Assert.Equal(new ProjectContextUsage(), context.Usage);
        Assert.Equal(["first", "second", "third"], context.Messages!.Select(GetMessageText));
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
            var deleted = await service.DeleteAsync(projectId, "context-1");
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
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var bindingService = new CapturingTaskSessionBindingService();
        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext, bindingService);

        var deleted = await service.DeleteAsync(projectId, "context-1");

        Assert.True(deleted);
        Assert.Equal([contextId], bindingService.DeletedContextIds);
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
            seedContext.AgentUsages.Add(CreateUsage(projectId, "context-1", "planner", 10, 20, 30, 4, 5));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var bindingService = new CapturingTaskSessionBindingService();
        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext, bindingService);

        var result = await service.DeleteAllAsync(projectId);

        Assert.Equal(ApplicationResultType.Success, result.Type);
        Assert.Equal(
            new[] { firstContextId, secondContextId }.OrderBy(id => id),
            bindingService.DeletedContextIds.OrderBy(id => id)
        );
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
                    UserName = "tester",
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

        var result = await service.ClearRecordsAsync(projectId, "context-1");

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

        var clearResult = await service.ClearRecordsAsync(projectId, "context-1");
        var contexts = await service.ListResponsesAsync(projectId);

        Assert.Equal(ApplicationResultType.Success, clearResult.Type);
        var context = Assert.Single(contexts);
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

    private static ProjectContextAppService CreateService(
        AgwDbContext dbContext,
        ITaskSessionBindingService? taskSessionBindingService = null
    )
    {
        var projectRepository = new EfRepository<Project>(dbContext);

        return new ProjectContextAppService(
            new EfRepository<ProjectConversation>(dbContext),
            new EfRepository<ProjectConversationChatHistory>(dbContext),
            new EfRepository<AgentflowCheckpointRecord>(dbContext),
            new EfRepository<AgentflowTrace>(dbContext),
            new EfRepository<AgentUsage>(dbContext),
            dbContext,
            new ProjectResolver(projectRepository),
            new ProjectConversationChatHistoryDomainService(),
            taskSessionBindingService
                ?? new TaskSessionBindingService(
                    new EfRepository<TaskSessionBinding>(dbContext),
                    new EfRepository<ProjectConversation>(dbContext),
                    dbContext,
                    TimeProvider.System
                ),
            TimeProvider.System
        );
    }

    private sealed class CapturingTaskSessionBindingService : ITaskSessionBindingService
    {
        public List<Guid> DeletedContextIds { get; } = [];

        public Task<TaskSessionBinding?> GetAsync(
            Guid projectId,
            string contextId,
            Guid agentId,
            string externalAgentName,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<TaskSessionBinding?>(null);

        public Task<TaskSessionBinding> UpsertAsync(
            Guid projectId,
            string contextId,
            Guid agentId,
            string externalAgentName,
            string providerSessionId,
            string user,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task DeleteByContextAsync(Guid projectConversationId, CancellationToken cancellationToken = default)
        {
            DeletedContextIds.Add(projectConversationId);
            return Task.CompletedTask;
        }
    }
}
