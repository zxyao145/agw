using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Projects.Application.Facades;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Domain.Services;
using Agw.Shared.Contracts.Conversations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

/// <summary>
/// 解析任务时记录与检查对话创建方：新对话记录请求方，extension 只能写入自己创建的对话与开放对话。
/// Recording and checking the conversation creator during task resolution: a new conversation records the requester, and an extension may write only its own and open conversations.
/// </summary>
public partial class TaskAppServiceTests
{
    [Fact]
    public async Task ResolveTaskAsync_NewConversationForExtension_RecordsCreatorExtension()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(token, CreateProject(projectId));
        await using var dbContext = database.CreateContext();

        var result = await CreateService(dbContext)
            .ResolveTaskAsync(CreateRequest(projectId, conversationId, "sample"), token);

        Assert.Null(result.Error);
        Assert.Equal("sample", (await ReadConversationAsync(database, conversationId)).ExtensionId);
    }

    [Fact]
    public async Task ResolveTaskAsync_NewConversationWithoutExtension_RecordsHostCreator()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(token, CreateProject(projectId));
        await using var dbContext = database.CreateContext();

        var result = await CreateService(dbContext)
            .ResolveTaskAsync(CreateRequest(projectId, conversationId, requesterExtensionId: null), token);

        Assert.Null(result.Error);
        Assert.Equal(ConversationCreators.Host, (await ReadConversationAsync(database, conversationId)).ExtensionId);
    }

    [Fact]
    public async Task ResolveTaskAsync_HostContinuesExtensionConversation_KeepsCreatorExtension()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(
            token,
            CreateProject(projectId),
            CreateConversationBy(conversationId, projectId, "sample"),
            CreateTurn(conversationId)
        );
        await using var dbContext = database.CreateContext();

        var result = await CreateService(dbContext)
            .ResolveTaskAsync(CreateRequest(projectId, conversationId, requesterExtensionId: null), token);

        Assert.Null(result.Error);
        Assert.Equal("sample", (await ReadConversationAsync(database, conversationId)).ExtensionId);
    }

    [Fact]
    public async Task ResolveTaskAsync_ExtensionContinuesOwnConversation_ReusesConversation()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(
            token,
            CreateProject(projectId),
            CreateConversationBy(conversationId, projectId, "sample"),
            CreateTurn(conversationId)
        );
        await using var dbContext = database.CreateContext();

        var result = await CreateService(dbContext)
            .ResolveTaskAsync(CreateRequest(projectId, conversationId, "sample"), token);

        Assert.Null(result.Error);
        Assert.Equal(conversationId, result.Task!.ProjectConversationId);
        Assert.Equal("sample", (await ReadConversationAsync(database, conversationId)).ExtensionId);
        Assert.Single(await dbContext.ProjectConversationChatHistories.ToListAsync(token));
    }

    [Fact]
    public async Task ResolveTaskAsync_ExtensionOnHostConversationWithAcceptedTurn_ThrowsConversationReadOnlyWithoutWrites()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(
            token,
            CreateProject(projectId),
            CreateConversationBy(conversationId, projectId, ConversationCreators.Host),
            CreateTurn(conversationId)
        );

        await AssertReadOnlyWithoutWritesAsync(database, projectId, conversationId, "sample");
    }

    [Fact]
    public async Task ResolveTaskAsync_ExtensionOnOtherExtensionConversation_ThrowsConversationReadOnlyWithoutWrites()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        // 另一个 extension 的第一个 Turn 受理失败：对话已保存，没有 Turn。
        // The other extension's first turn failed acceptance: the conversation is saved without turns.
        await database.SeedAsync(
            token,
            CreateProject(projectId),
            CreateConversationBy(conversationId, projectId, "other")
        );

        await AssertReadOnlyWithoutWritesAsync(database, projectId, conversationId, "sample");
    }

    [Fact]
    public async Task ResolveTaskAsync_ExtensionOnOpenConversation_RecordsCreatorExtension()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(
            token,
            CreateProject(projectId),
            CreateConversationBy(conversationId, projectId, ConversationCreators.Host)
        );
        await using var dbContext = database.CreateContext();

        var result = await CreateService(dbContext)
            .ResolveTaskAsync(CreateRequest(projectId, conversationId, "sample"), token);

        Assert.Null(result.Error);
        Assert.Equal("sample", (await ReadConversationAsync(database, conversationId)).ExtensionId);
        Assert.Single(await dbContext.ProjectConversationChatHistories.ToListAsync(token));
    }

    [Fact]
    public async Task ResolveTaskAsync_ExtensionOnClearedHostConversation_ThrowsConversationReadOnly()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(
            token,
            CreateProject(projectId),
            CreateConversationBy(conversationId, projectId, ConversationCreators.Host, generation: 1)
        );

        await AssertReadOnlyWithoutWritesAsync(database, projectId, conversationId, "sample");
    }

    [Fact]
    public async Task ResolveTaskAsync_ConcurrentInsertOfHostConversationWithAcceptedTurn_ThrowsConversationReadOnlyForExtension()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(token, CreateProject(projectId));
        await using var dbContext = database.CreateContext();
        var unitOfWork = new ConcurrentConversationInsertUnitOfWork(
            dbContext,
            database.Options,
            CreateConversationBy(conversationId, projectId, ConversationCreators.Host),
            CreateTurn(conversationId)
        );
        var service = CreateService(dbContext, unitOfWork);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.ResolveTaskAsync(CreateRequest(projectId, conversationId, "sample"), token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        Assert.Equal(ConversationCreators.Host, (await ReadConversationAsync(database, conversationId)).ExtensionId);
        Assert.Empty(await ReadHistoriesAsync(database, conversationId));
    }

    [Fact]
    public async Task ResolveTaskAsync_ConcurrentInsertBySameExtension_ReusesConversation()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(token, CreateProject(projectId));
        await using var dbContext = database.CreateContext();
        var unitOfWork = new ConcurrentConversationInsertUnitOfWork(
            dbContext,
            database.Options,
            CreateConversationBy(conversationId, projectId, "sample")
        );

        var result = await CreateService(dbContext, unitOfWork)
            .ResolveTaskAsync(CreateRequest(projectId, conversationId, "sample"), token);

        Assert.Null(result.Error);
        Assert.Equal(conversationId, result.Task!.ProjectConversationId);
        Assert.Equal("sample", (await ReadConversationAsync(database, conversationId)).ExtensionId);
        Assert.Single(await ReadHistoriesAsync(database, conversationId));
    }

    [Fact]
    public async Task ResolveTaskAsync_OpenConversationClaimedAndAcceptedByOtherExtension_ThrowsConversationReadOnlyWithoutWrites()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(
            token,
            CreateProject(projectId),
            CreateConversationBy(conversationId, projectId, ConversationCreators.Host)
        );
        await using (var first = database.CreateContext())
        {
            var claimed = await CreateService(first)
                .ResolveTaskAsync(CreateRequest(projectId, conversationId, "first"), token);
            Assert.Null(claimed.Error);
        }
        await database.SeedAsync(token, CreateTurn(conversationId));

        await AssertReadOnlyWithoutWritesAsync(database, projectId, conversationId, "second");
        Assert.Equal("first", (await ReadConversationAsync(database, conversationId)).ExtensionId);
    }

    [Fact]
    public async Task ResolveAsync_RequesterExtension_RecordsCreatorOnNewConversation()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(token, CreateProject(projectId));
        await using var dbContext = database.CreateContext();
        var userInfo = new TestUserInfoService();
        var projectResolver = new ProjectResolver(dbContext, userInfo);
        var taskExecution = new TaskExecutionAppService(
            dbContext,
            projectResolver,
            new ConversationHistoryDomainService(),
            TimeProvider.System,
            userInfo,
            new ConversationCreatorStore(dbContext)
        );
        var facade = new ProjectTaskFacade(
            taskExecution,
            dbContext,
            new TaskAppService(dbContext, projectResolver, taskExecution, userInfo),
            userInfo
        );

        var task = await facade.ResolveAsync(
            new ResolveProjectTaskRequest(
                TaskId: null,
                ConversationId: conversationId,
                ProjectId: projectId,
                Input: "hello world",
                Resume: false,
                OwnerUserId: "tester",
                RequesterExtensionId: "sample"
            ),
            token
        );

        Assert.Equal(conversationId, task.ProjectConversationId);
        Assert.Equal("sample", (await ReadConversationAsync(database, conversationId)).ExtensionId);
    }

    /// <summary>
    /// 请求方被拒绝时对话的历史记录、标题与更新信息都不变。
    /// A refused requester leaves the conversation's history, title and update stamps unchanged.
    /// </summary>
    private static async Task AssertReadOnlyWithoutWritesAsync(
        SqliteTestDatabase database,
        Guid projectId,
        Guid conversationId,
        string requesterExtensionId
    )
    {
        var token = TestContext.Current.CancellationToken;
        await using var dbContext = database.CreateContext();
        var service = CreateService(dbContext);
        var before = await ReadConversationAsync(database, conversationId);
        var historiesBefore = (await ReadHistoriesAsync(database, conversationId)).Count;

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.ResolveTaskAsync(CreateRequest(projectId, conversationId, requesterExtensionId, "new title"), token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        var after = await ReadConversationAsync(database, conversationId);
        Assert.Equal(before.ExtensionId, after.ExtensionId);
        Assert.Equal(before.Title, after.Title);
        Assert.Equal(before.UpdateBy, after.UpdateBy);
        Assert.Equal(before.UpdateTime, after.UpdateTime);
        Assert.Equal(historiesBefore, (await ReadHistoriesAsync(database, conversationId)).Count);
    }

    private static ExecutionTaskRequest CreateRequest(
        Guid projectId,
        Guid conversationId,
        string? requesterExtensionId,
        string input = "hello world"
    ) =>
        new(
            TaskId: null,
            ConversationId: conversationId,
            ProjectId: projectId,
            ContextId: null,
            Input: input,
            Resume: false,
            User: "tester",
            RequesterExtensionId: requesterExtensionId
        );

    private static ProjectConversation CreateConversationBy(
        Guid conversationId,
        Guid projectId,
        string extensionId,
        int generation = 0
    )
    {
        var conversation = CreateConversation(conversationId, projectId, $"context-{conversationId:N}");
        conversation.ExtensionId = extensionId;
        conversation.Generation = generation;
        return conversation;
    }

    private static ProjectConversationTurn CreateTurn(Guid conversationId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectConversationId = conversationId,
            TargetId = Guid.CreateVersion7(),
            Status = ProjectConversationTurnStatus.Completed,
            InputMessageId = Guid.CreateVersion7(),
            StartedAt = TimeProvider.System.GetUtcNow(),
            FinishedAt = TimeProvider.System.GetUtcNow(),
        };

    private static async Task<ProjectConversation> ReadConversationAsync(
        SqliteTestDatabase database,
        Guid conversationId
    )
    {
        await using var context = database.CreateContext();
        return await context
            .ProjectConversations.AsNoTracking()
            .SingleAsync(conversation => conversation.Id == conversationId, TestContext.Current.CancellationToken);
    }

    private static async Task<List<ProjectConversationChatHistory>> ReadHistoriesAsync(
        SqliteTestDatabase database,
        Guid conversationId
    )
    {
        await using var context = database.CreateContext();
        return await context
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record => record.ConversationId == conversationId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
