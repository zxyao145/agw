using System.Security.Claims;
using Agw.Auth.Application;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Projects.Domain.Services;
using Agw.Shared.Contracts.Conversations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class TaskExecutionAppServiceTests
{
    [Fact]
    public void TaskCreateRequest_RemovesLegacyTargetBindingConstructor()
    {
        Assert.DoesNotContain(
            typeof(TaskCreateRequest).GetConstructors(),
            constructor =>
            {
                var parameters = constructor.GetParameters();
                return parameters.Length == 7
                    && parameters[0].Name == "AgentType"
                    && parameters[1].Name == "AgentflowId"
                    && parameters[2].Name == "AgentId"
                    && parameters[3].Name == "Description";
            }
        );
    }

    [Fact]
    public async Task CreateRunningAsync_CreatesContextAndProjectConversationChatHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Jobs Project"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var result = await service.CreateRunningAsync(
            projectId,
            new TaskCreateRequest(Input: "Run task", Title: "Task", ContextId: "context-1"),
            "tester"
        );

        Assert.Equal(ApplicationResultType.Success, result.Type);
        var response = Assert.IsType<TaskExecutionSnapshot>(result.Value);
        Assert.Equal(TaskExecutionStatus.Running, response.Status);
        Assert.Equal(projectId.Normalize(), response.ProjectId);
        Assert.Equal("context-1", response.ContextId);
        Assert.Equal("Task", response.Title);
        Assert.NotEqual(Guid.Empty, response.TaskId);

        var context = await dbContext.ProjectConversations.SingleAsync(cancellationToken);
        Assert.Equal("context-1", context.ContextId);
        Assert.Equal("Task", context.Title);
        Assert.Equal(ConversationKinds.Native, context.Kind);
        Assert.Null(context.RelateId);

        var record = await dbContext.ProjectConversationChatHistories.SingleAsync(cancellationToken);
        Assert.Equal(context.Id, record.ConversationId);
        Assert.Equal(response.TaskId, record.TaskId);
        Assert.Equal(TaskExecutionStatus.Running, record.Status);
        Assert.Null(record.ConversationPayload);
    }

    [Fact]
    public async Task CreateRunningForExecutionAsync_RecordsRequesterExtensionAsConversationCreator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Background Project"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "test"))
        );
        var userInfo = new UserInfoService();
        var service = new TaskExecutionAppService(
            dbContext,
            new ProjectResolver(dbContext, userInfo),
            new ConversationHistoryDomainService(),
            TimeProvider.System,
            userInfo,
            new ConversationCreatorStore(dbContext)
        );
        const string extensionId = "11111111-1111-1111-1111-111111111111";

        var result = await service.CreateRunningForExecutionAsync(
            projectId,
            Guid.CreateVersion7(),
            new TaskCreateRequest(
                Input: "background input",
                Title: "background title",
                ContextId: "background-context"
            ),
            "tester",
            extensionId
        );

        Assert.Equal(ApplicationResultType.Success, result.Type);
        var conversation = await dbContext.ProjectConversations.SingleAsync(cancellationToken);
        Assert.Equal(extensionId, conversation.ExtensionId);
        Assert.Equal(ConversationKinds.Native, conversation.Kind);
        Assert.Null(conversation.RelateId);
    }

    [Fact]
    public async Task CreateRunningForExecutionAsync_EventKind_CreatesConversationWithKindAndRelateId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Event Project"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);
        var eventId = Guid.CreateVersion7();
        var result = await service.CreateRunningForExecutionAsync(
            projectId,
            Guid.CreateVersion7(),
            new TaskCreateRequest(
                Input: "timer event input",
                Title: "Timer event",
                ContextId: "timer-context",
                Kind: "event:timer",
                RelateId: eventId
            ),
            "tester"
        );

        Assert.Equal(ApplicationResultType.Success, result.Type);
        var conversation = await dbContext.ProjectConversations.SingleAsync(cancellationToken);
        Assert.Equal("event:timer", conversation.Kind);
        Assert.Equal(eventId, conversation.RelateId);
    }

    [Fact]
    public async Task CreateRunningForExecutionAsync_ExistingConversation_KeepsKindAndRelateId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Event Project"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);
        var firstResult = await service.CreateRunningForExecutionAsync(
            projectId,
            Guid.CreateVersion7(),
            new TaskCreateRequest(Input: "native input", ContextId: "shared-context"),
            "tester"
        );
        Assert.Equal(ApplicationResultType.Success, firstResult.Type);
        var conversation = await dbContext.ProjectConversations.SingleAsync(cancellationToken);
        var eventId = Guid.CreateVersion7();

        var secondResult = await service.CreateRunningForExecutionAsync(
            projectId,
            Guid.CreateVersion7(),
            new TaskCreateRequest(
                Input: "event input",
                ContextId: "shared-context",
                Kind: "event:timer",
                RelateId: eventId
            ),
            "tester"
        );

        Assert.Equal(ApplicationResultType.Success, secondResult.Type);
        Assert.Equal(ConversationKinds.Native, conversation.Kind);
        Assert.Null(conversation.RelateId);
    }

    [Fact]
    public async Task CreateForExecutionAsync_WhenContextIdMissing_DoesNotUseTaskIdAsContextId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Jobs Project"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);
        var taskId = Guid.CreateVersion7();

        var result = await service.CreateForExecutionAsync(
            projectId,
            taskId,
            new TaskCreateRequest(Input: "Run task", Title: "Task"),
            "tester"
        );

        Assert.Equal(ApplicationResultType.Success, result.Type);
        var response = Assert.IsType<TaskExecutionSnapshot>(result.Value);
        Assert.Equal(taskId, response.TaskId);
        Assert.NotEqual(taskId.Normalize(), response.ContextId);
    }

    [Fact]
    public async Task CreateForExecutionAsync_GuidContextIdWithDifferentCasing_ReusesCanonicalContext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Context Project"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);
        var contextGuid = Guid.CreateVersion7();

        var uppercaseResult = await service.CreateForExecutionAsync(
            projectId,
            taskId: null,
            new TaskCreateRequest(Input: "First task", ContextId: contextGuid.ToString("D").ToUpperInvariant()),
            "tester"
        );
        var lowercaseResult = await service.CreateForExecutionAsync(
            projectId,
            taskId: null,
            new TaskCreateRequest(Input: "Second task", ContextId: contextGuid.Normalize()),
            "tester"
        );

        Assert.Equal(ApplicationResultType.Success, uppercaseResult.Type);
        Assert.Equal(ApplicationResultType.Success, lowercaseResult.Type);
        var context = Assert.Single(await dbContext.ProjectConversations.ToListAsync(cancellationToken));
        Assert.Equal(contextGuid.Normalize(), context.ContextId);
        Assert.Equal(2, await dbContext.ProjectConversationChatHistories.CountAsync(cancellationToken));
        Assert.All(
            await dbContext.ProjectConversationChatHistories.ToListAsync(cancellationToken),
            record => Assert.Equal(context.Id, record.ConversationId)
        );
    }

    [Fact]
    public async Task CreateForExecutionAsync_WithLegacyUppercaseGuidContext_LeavesOldContextUntouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var contextGuid = Guid.CreateVersion7();
        var context = CreateContext(
            Guid.CreateVersion7(),
            projectId,
            contextGuid.ToString("D").ToUpperInvariant(),
            "Legacy context"
        );
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Context Project"));
            seedContext.ProjectConversations.Add(context);
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var result = await service.CreateForExecutionAsync(
            projectId,
            taskId: null,
            new TaskCreateRequest(Input: "Continue task", ContextId: contextGuid.Normalize()),
            "tester"
        );

        Assert.Equal(ApplicationResultType.Success, result.Type);
        var contexts = await dbContext.ProjectConversations.ToListAsync(cancellationToken);
        Assert.Equal(2, contexts.Count);
        Assert.Equal(
            contextGuid.ToString("D").ToUpperInvariant(),
            contexts.Single(item => item.Id == context.Id).ContextId
        );
        Assert.Equal(contextGuid.Normalize(), contexts.Single(item => item.Id != context.Id).ContextId);
    }

    [Fact]
    public async Task ClearRecordsAsync_WhenTaskBelongsToProject_RemovesOnlyProjectConversationChatHistories()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var contextId = Guid.CreateVersion7();
        var otherContextId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var otherTaskId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId, "Project"));
            seedContext.ProjectConversations.AddRange(
                CreateContext(contextId, projectId, "context-1", "Task"),
                CreateContext(otherContextId, projectId, "context-2", "Other Task")
            );
            seedContext.ProjectConversationChatHistories.AddRange(
                CreateRecord(contextId, taskId),
                CreateRecord(contextId, taskId),
                CreateRecord(otherContextId, otherTaskId)
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var result = await service.ClearRecordsAsync(projectId, taskId);

        Assert.Equal(ApplicationResultType.Success, result.Type);
        Assert.Empty(
            await dbContext
                .ProjectConversationChatHistories.Where(record => record.TaskId == taskId)
                .ToListAsync(cancellationToken)
        );
        var remainingRecord = await dbContext.ProjectConversationChatHistories.SingleAsync(cancellationToken);
        Assert.Equal(otherTaskId, remainingRecord.TaskId);
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

    private static ProjectConversation CreateContext(Guid id, Guid projectId, string contextId, string title) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            ContextId = contextId,
            Title = title,
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
            UpdateBy = "tester",
            UpdateTime = TimeProvider.System.GetUtcNow(),
        };

    private static ProjectConversationChatHistory CreateRecord(Guid contextId, Guid taskId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ConversationId = contextId,
            TaskId = taskId,
            Status = TaskExecutionStatus.Running,
            CreateTime = TimeProvider.System.GetUtcNow(),
            UpdateTime = TimeProvider.System.GetUtcNow(),
        };

    private static TaskExecutionAppService CreateService(AgwDbContext dbContext)
    {
        var userInfo = new TestUserInfoService();
        var projectResolver = new ProjectResolver(dbContext, userInfo);

        return new TaskExecutionAppService(
            dbContext,
            projectResolver,
            new ConversationHistoryDomainService(),
            TimeProvider.System,
            userInfo,
            new ConversationCreatorStore(dbContext)
        );
    }
}
