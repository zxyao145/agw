using Agw.Agents.Execution;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Projects.Tests;

public sealed class ProjectConversationImageAppServiceTests
{
    private const string ImagePath = "/tmp/pelican-on-bicycle.svg.png";
    private const string ThreadId = "01a17e18-9941-76d0-8569-05a4a4a9e63d";
    private const string ProviderTurnId = "01a17e18-9f79-7303-a5ec-8b3fa0b52a5f";
    private const string AgwTurnId = "01a17e18-9425-7674-bc53-12463e3c5d39";
    private const string ProducerScopeId = "01a17e18-98f6-7373-9ffa-5132ef07ef16";
    private static readonly byte[] InitialImageBytes = ReadImage("image-view-1.png");
    private static readonly byte[] UpdatedImageBytes = ReadImage("image-view-2.png");

    [Fact]
    public async Task GetDataUriAsync_ResultUsesLatestSavedImageFromSameTurnAndProducer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ImageTestScope.CreateAsync(cancellationToken);
        using var userScope = UserInfoUtil.Push(scope.UserInfo.Current);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var result = CreateResultMessage();
        var resultRecord = CreateRecord(conversationId, taskId, turnId, 6, result, ConversationMessagePurpose.Result);
        var records = new[]
        {
            CreateImageRecord(conversationId, taskId, turnId, 1, ImagePath, InitialImageBytes, ThreadId),
            CreateImageRecord(conversationId, taskId, turnId, 2, ImagePath, UpdatedImageBytes, ThreadId),
            CreateImageRecord(conversationId, taskId, Guid.CreateVersion7(), 3, ImagePath, InitialImageBytes, ThreadId),
            CreateImageRecord(conversationId, Guid.CreateVersion7(), turnId, 4, ImagePath, InitialImageBytes, ThreadId),
            CreateImageRecord(conversationId, taskId, turnId, 5, ImagePath, InitialImageBytes, "another-thread"),
            resultRecord,
            CreateImageRecord(conversationId, taskId, turnId, 7, ImagePath, InitialImageBytes, ThreadId),
        };
        await SeedAsync(scope.DbContext, projectId, conversationId, records, cancellationToken);

        var dataUri = await scope.Service.GetDataUriAsync(
            projectId,
            conversationId,
            resultRecord.Id.ToString("D"),
            ImagePath,
            cancellationToken
        );

        Assert.Equal(new DataContent(UpdatedImageBytes, "image/png").Uri, dataUri);
    }

    [Fact]
    public async Task GetDataUriAsync_NoMatchingSavedImage_ReturnsNull()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ImageTestScope.CreateAsync(cancellationToken);
        using var userScope = UserInfoUtil.Push(scope.UserInfo.Current);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var resultRecord = CreateRecord(
            conversationId,
            taskId,
            turnId,
            2,
            CreateResultMessage(),
            ConversationMessagePurpose.Result
        );
        await SeedAsync(
            scope.DbContext,
            projectId,
            conversationId,
            [
                CreateImageRecord(
                    conversationId,
                    taskId,
                    turnId,
                    1,
                    "/workspace/other.png",
                    InitialImageBytes,
                    ThreadId
                ),
                resultRecord,
            ],
            cancellationToken
        );

        var dataUri = await scope.Service.GetDataUriAsync(
            projectId,
            conversationId,
            resultRecord.Id.ToString("D"),
            ImagePath,
            cancellationToken
        );

        Assert.Null(dataUri);
    }

    [Fact]
    public async Task GetDataUriAsync_ProjectOwnedByAnotherUser_ThrowsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ImageTestScope.CreateAsync(cancellationToken);
        using var userScope = UserInfoUtil.Push(scope.UserInfo.Current);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var project = CreateProject(projectId);
        project.CreateBy = "another-user";
        var conversation = CreateConversation(conversationId, projectId);
        conversation.CreateBy = "another-user";
        var resultRecord = CreateRecord(
            conversationId,
            taskId,
            turnId,
            1,
            CreateResultMessage(),
            ConversationMessagePurpose.Result
        );

        scope.DbContext.Projects.Add(project);
        scope.DbContext.ProjectConversations.Add(conversation);
        scope.DbContext.ProjectConversationChatHistories.Add(resultRecord);
        await scope.DbContext.SaveChangesAsync(cancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Service.GetDataUriAsync(
                projectId,
                conversationId,
                resultRecord.Id.ToString("D"),
                ImagePath,
                cancellationToken
            )
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
    }

    [Fact]
    public async Task GetDataUriAsync_DeletedAgent_ThrowsNotFoundBeforeStartingCodex()
    {
        await AssertUnavailableAgentAsync(addForeignAgent: false);
    }

    [Fact]
    public async Task GetDataUriAsync_ForeignAgent_IsHiddenByUserScopeBeforeStartingCodex()
    {
        await AssertUnavailableAgentAsync(addForeignAgent: true);
    }

    private static async Task AssertUnavailableAgentAsync(bool addForeignAgent)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ImageTestScope.CreateAsync(cancellationToken);
        using var userScope = UserInfoUtil.Push(scope.UserInfo.Current);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var imageRecord = CreateImageRecord(
            conversationId,
            taskId,
            turnId,
            1,
            imagePath: null,
            InitialImageBytes,
            ThreadId,
            agentId
        );
        var resultRecord = CreateRecord(
            conversationId,
            taskId,
            turnId,
            2,
            CreateResultMessage(),
            ConversationMessagePurpose.Result,
            agentId
        );

        scope.DbContext.Projects.Add(CreateProject(projectId));
        scope.DbContext.ProjectConversations.Add(CreateConversation(conversationId, projectId));
        if (addForeignAgent)
            scope.DbContext.Agents.Add(CreateAgent(agentId, "another-user"));
        scope.DbContext.ProjectConversationChatHistories.AddRange(imageRecord, resultRecord);
        await scope.DbContext.SaveChangesAsync(cancellationToken);

        if (addForeignAgent)
        {
            Assert.Null(
                await scope
                    .DbContext.Agents.AsNoTracking()
                    .SingleOrDefaultAsync(agent => agent.Id == agentId, cancellationToken)
            );
        }

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Service.GetDataUriAsync(
                projectId,
                conversationId,
                resultRecord.Id.ToString("D"),
                ImagePath,
                cancellationToken
            )
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
    }

    private static async Task SeedAsync(
        AgwDbContext dbContext,
        Guid projectId,
        Guid conversationId,
        IReadOnlyCollection<ProjectConversationChatHistory> records,
        CancellationToken cancellationToken
    )
    {
        dbContext.Projects.Add(CreateProject(projectId));
        dbContext.ProjectConversations.Add(CreateConversation(conversationId, projectId));
        dbContext.ProjectConversationChatHistories.AddRange(records);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static Project CreateProject(Guid id) =>
        new()
        {
            Id = id,
            Name = "Result image history",
            Type = ProjectType.UserDefined,
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static ProjectConversation CreateConversation(Guid id, Guid projectId) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            ContextId = $"conversation-{id:N}",
            Title = "Pelican image Result",
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
            UpdateBy = "tester",
            UpdateTime = TimeProvider.System.GetUtcNow(),
        };

    private static ProjectConversationChatHistory CreateImageRecord(
        Guid conversationId,
        Guid taskId,
        Guid turnId,
        long sequence,
        string? imagePath,
        byte[] imageBytes,
        string threadId,
        Guid? agentId = null
    )
    {
        var image = CreateImage(imageBytes, threadId);
        if (imagePath is not null)
            image.AdditionalProperties!["imagePath"] = imagePath;
        return CreateRecord(
            conversationId,
            taskId,
            turnId,
            sequence,
            new ChatMessage(ChatRole.Assistant, [image]) { AdditionalProperties = CreateMessageProperties(threadId) },
            ConversationMessagePurpose.Message,
            agentId
        );
    }

    private static ProjectConversationChatHistory CreateRecord(
        Guid conversationId,
        Guid taskId,
        Guid turnId,
        long sequence,
        ChatMessage message,
        ConversationMessagePurpose purpose,
        Guid? agentId = null
    )
    {
        var id = Guid.CreateVersion7();
        message.MessageId = id.ToString("D");
        return new ProjectConversationChatHistory
        {
            Id = id,
            ConversationId = conversationId,
            TaskId = taskId,
            AgentId = agentId,
            TurnId = turnId,
            ConversationSequence = sequence,
            Purpose = purpose,
            Status = TaskExecutionStatus.Succeeded,
            ConversationPayload = JsonUtil.Serialize(message),
            CreateTime = TimeProvider.System.GetUtcNow(),
        };
    }

    private static ChatMessage CreateResultMessage()
    {
        var properties = CreateMessageProperties(ThreadId);
        properties["type"] = "result";
        return new ChatMessage(ChatRole.Assistant, [new TextContent($"![A pelican riding a bicycle]({ImagePath})")])
        {
            AdditionalProperties = properties,
        };
    }

    private static DataContent CreateImage(byte[] imageBytes, string threadId) =>
        new(imageBytes, "image/png")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["threadId"] = threadId,
                ["turnId"] = ProviderTurnId,
                ["itemId"] = "exec-e4c67153-873b-405f-844a-75e41e3507cc",
                ["imageSource"] = "imageView",
            },
        };

    private static Agent CreateAgent(Guid id, string owner) =>
        new()
        {
            Id = id,
            Name = $"agent-{id:N}",
            DisplayName = $"Agent {id:N}",
            CreateBy = owner,
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static AdditionalPropertiesDictionary CreateMessageProperties(string threadId) =>
        new()
        {
            ["threadId"] = threadId,
            ["turnId"] = AgwTurnId,
            ["producerScopeId"] = ProducerScopeId,
            ["stepIndex"] = 3,
        };

    private static byte[] ReadImage(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "CodexAppServer", fileName));

    private sealed class ImageTestScope : IAsyncDisposable
    {
        private readonly ServiceProvider _readerProvider;
        private readonly SqliteConnection _connection;

        private ImageTestScope(
            SqliteConnection connection,
            AgwDbContext dbContext,
            ServiceProvider readerProvider,
            ProjectConversationImageAppService service,
            TestUserInfoService userInfo
        )
        {
            _connection = connection;
            DbContext = dbContext;
            _readerProvider = readerProvider;
            Service = service;
            UserInfo = userInfo;
        }

        public AgwDbContext DbContext { get; }

        public ProjectConversationImageAppService Service { get; }

        public TestUserInfoService UserInfo { get; }

        public static async Task<ImageTestScope> CreateAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            var dbContext = new AgwDbContext(options);
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            var userInfo = new TestUserInfoService();
            var readerProvider = new ServiceCollection()
                .AddSingleton<Agw.Agents.Application.Persistence.IAgentsDbContext>(dbContext)
                .AddAgentExecution(
                    new ConfigurationBuilder().Build(),
                    new Agw.Agents.Execution.DependencyInjection.RegistrationOptions(
                        AddExecutionTransport: false,
                        AddDistributedWorker: false,
                        AddTraceCollector: false
                    )
                )
                .BuildServiceProvider();
            var projectResolver = new ProjectResolver(dbContext, userInfo);
            var service = new ProjectConversationImageAppService(
                dbContext,
                projectResolver,
                readerProvider.GetRequiredService<ICodexImagePathReader>()
            );
            return new ImageTestScope(connection, dbContext, readerProvider, service, userInfo);
        }

        public async ValueTask DisposeAsync()
        {
            await _readerProvider.DisposeAsync();
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
