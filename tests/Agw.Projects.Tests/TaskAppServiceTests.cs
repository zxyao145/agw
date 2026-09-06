using Agw.Infrastructure.Data;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public partial class TaskAppServiceTests
{
    [Fact]
    public void ITaskAppService_ExposesOnlyReducedCreateTaskForExecutionSignature()
    {
        var methods = typeof(ITaskAppService)
            .GetMethods()
            .Where(method => method.Name == nameof(ITaskAppService.CreateTaskForExecutionAsync))
            .ToArray();

        var method = Assert.Single(methods);
        var parameters = method.GetParameters();

        Assert.Collection(
            parameters,
            parameter => Assert.Equal("projectId", parameter.Name),
            parameter => Assert.Equal("taskId", parameter.Name),
            parameter => Assert.Equal("input", parameter.Name),
            parameter => Assert.Equal("user", parameter.Name),
            parameter => Assert.Equal("contextId", parameter.Name),
            parameter => Assert.Equal("cancellationToken", parameter.Name)
        );
    }

    [Fact]
    public async Task CreateTaskForExecutionAsync_CreatesChatTaskWithoutTargetBinding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = "Chat Project",
                    Type = ProjectType.UserDefined,
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var task = await service.CreateTaskForExecutionAsync(
            projectId,
            taskId: null,
            input: "  hello world  ",
            user: "tester",
            cancellationToken: cancellationToken
        );

        Assert.NotNull(task);
        Assert.Null(task!.JobId);
        Assert.Equal("hello world", task.Title);
        Assert.NotNull(await dbContext.ProjectConversations.SingleOrDefaultAsync(cancellationToken));
        Assert.NotNull(
            await dbContext.ProjectConversationChatHistories.SingleOrDefaultAsync(
                record => record.TaskId == task.TaskId,
                cancellationToken
            )
        );
    }

    [Fact]
    public async Task ResolveTaskAsync_NewConversationId_PersistsConversationAndTaskRecord()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(cancellationToken);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(cancellationToken, CreateProject(projectId));

        await using var dbContext = database.CreateContext();
        var service = CreateService(dbContext);

        var result = await service.ResolveTaskAsync(
            new ExecutionTaskRequest(
                TaskId: null,
                ConversationId: conversationId,
                ProjectId: projectId,
                ContextId: "context-new",
                Input: "hello world",
                Resume: false,
                User: "tester"
            ),
            cancellationToken
        );

        Assert.Null(result.Error);
        var task = Assert.IsType<TaskProjection>(result.Task);
        Assert.Equal(conversationId, task.ProjectConversationId);
        var conversation = await dbContext.ProjectConversations.SingleAsync(cancellationToken);
        Assert.Equal(conversationId, conversation.Id);
        Assert.Equal(projectId, conversation.ProjectId);
        Assert.Equal("context-new", conversation.ContextId);
        Assert.Equal("tester", conversation.CreateBy);
        var record = await dbContext.ProjectConversationChatHistories.SingleAsync(cancellationToken);
        Assert.Equal(conversationId, record.ConversationId);
        Assert.Equal(task.TaskId, record.TaskId);
    }

    [Fact]
    public async Task ResolveTaskAsync_ContextOwnedByDifferentConversationId_ThrowsInvalidParam()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(cancellationToken);
        var projectId = Guid.CreateVersion7();
        await database.SeedAsync(
            cancellationToken,
            CreateProject(projectId),
            CreateConversation(Guid.CreateVersion7(), projectId, "context-existing")
        );

        await using var dbContext = database.CreateContext();
        var service = CreateService(dbContext);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.ResolveTaskAsync(
                new ExecutionTaskRequest(
                    TaskId: null,
                    ConversationId: Guid.CreateVersion7(),
                    ProjectId: projectId,
                    ContextId: "context-existing",
                    Input: "hello world",
                    Resume: false,
                    User: "tester"
                ),
                cancellationToken
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Single(await dbContext.ProjectConversations.ToListAsync(cancellationToken));
        Assert.Empty(await dbContext.ProjectConversationChatHistories.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task ResolveTaskAsync_ConversationIdWithDifferentContext_ThrowsInvalidParam()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(cancellationToken);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(
            cancellationToken,
            CreateProject(projectId),
            CreateConversation(conversationId, projectId, "context-existing")
        );

        await using var dbContext = database.CreateContext();
        var service = CreateService(dbContext);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.ResolveTaskAsync(
                new ExecutionTaskRequest(
                    TaskId: null,
                    ConversationId: conversationId,
                    ProjectId: projectId,
                    ContextId: "context-other",
                    Input: "hello world",
                    Resume: false,
                    User: "tester"
                ),
                cancellationToken
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Single(await dbContext.ProjectConversations.ToListAsync(cancellationToken));
        Assert.Empty(await dbContext.ProjectConversationChatHistories.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task ResolveTaskAsync_ExistingConversationIdAndContext_ReusesConversation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(cancellationToken);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(cancellationToken, CreateProject(projectId));

        await using var dbContext = database.CreateContext();
        var service = CreateService(dbContext);
        var firstRequest = new ExecutionTaskRequest(
            TaskId: null,
            ConversationId: conversationId,
            ProjectId: projectId,
            ContextId: "context-reused",
            Input: "first",
            Resume: false,
            User: "tester"
        );
        var secondRequest = firstRequest with { Input = "second" };

        var first = await service.ResolveTaskAsync(firstRequest, cancellationToken);
        var second = await service.ResolveTaskAsync(secondRequest, cancellationToken);

        Assert.Null(first.Error);
        Assert.Null(second.Error);
        Assert.Equal(conversationId, first.Task!.ProjectConversationId);
        Assert.Equal(conversationId, second.Task!.ProjectConversationId);
        Assert.Single(await dbContext.ProjectConversations.ToListAsync(cancellationToken));
        Assert.Equal(2, await dbContext.ProjectConversationChatHistories.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task ResolveTaskAsync_ConcurrentMatchingConversationInsert_ReusesConversation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(cancellationToken);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        const string contextId = "context-concurrent";
        await database.SeedAsync(cancellationToken, CreateProject(projectId));

        await using var dbContext = database.CreateContext();
        var unitOfWork = new ConcurrentConversationInsertUnitOfWork(
            dbContext,
            database.Options,
            CreateConversation(conversationId, projectId, contextId)
        );
        var service = CreateService(dbContext, unitOfWork);

        var result = await service.ResolveTaskAsync(
            new ExecutionTaskRequest(
                TaskId: null,
                ConversationId: conversationId,
                ProjectId: projectId,
                ContextId: contextId,
                Input: "hello world",
                Resume: false,
                User: "tester"
            ),
            cancellationToken
        );

        Assert.Null(result.Error);
        var task = Assert.IsType<TaskProjection>(result.Task);
        Assert.Equal(conversationId, task.ProjectConversationId);
        Assert.Single(await dbContext.ProjectConversations.ToListAsync(cancellationToken));
        var record = await dbContext.ProjectConversationChatHistories.SingleAsync(cancellationToken);
        Assert.Equal(conversationId, record.ConversationId);
        Assert.Equal(task.TaskId, record.TaskId);
    }

    [Fact]
    public async Task ResolveTaskAsync_ConversationIdOwnedByAnotherUser_ThrowsInvalidParam()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(cancellationToken);
        var projectId = Guid.CreateVersion7();
        var foreignProjectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await database.SeedAsync(
            cancellationToken,
            CreateProject(projectId),
            CreateProject(foreignProjectId, "other-user"),
            CreateConversation(conversationId, foreignProjectId, "foreign-context", "other-user")
        );

        await using (var dbContext = database.CreateContext())
        {
            var service = CreateService(dbContext);

            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                service.ResolveTaskAsync(
                    new ExecutionTaskRequest(
                        TaskId: null,
                        ConversationId: conversationId,
                        ProjectId: projectId,
                        ContextId: "new-context",
                        Input: "hello world",
                        Resume: false,
                        User: "tester"
                    ),
                    cancellationToken
                )
            );

            Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        }

        await using var assertContext = database.CreateContext();
        Assert.Equal(1, await assertContext.ProjectConversations.IgnoreQueryFilters().CountAsync(cancellationToken));
        Assert.Empty(
            await assertContext.ProjectConversationChatHistories.IgnoreQueryFilters().ToListAsync(cancellationToken)
        );
    }

    [Fact]
    public async Task ResolveTaskAsync_ResumeWithoutContextId_ReturnsInvalidResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(cancellationToken);
        var projectId = Guid.CreateVersion7();
        await database.SeedAsync(cancellationToken, CreateProject(projectId));

        await using var dbContext = database.CreateContext();
        var service = CreateService(dbContext);

        var result = await service.ResolveTaskAsync(
            new ExecutionTaskRequest(
                TaskId: null,
                ConversationId: Guid.CreateVersion7(),
                ProjectId: projectId,
                ContextId: null,
                Input: "resume",
                Resume: true,
                User: "tester"
            ),
            cancellationToken
        );

        Assert.Null(result.Task);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ResolveTaskAsync_WhenResumeUsesConversationId_ReturnsLatestTaskInProjectConversation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var projectId = Guid.CreateVersion7();
        var contextRowId = Guid.CreateVersion7();
        var oldTaskId = Guid.CreateVersion7();
        var latestTaskId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = "Chat Project",
                    Type = ProjectType.UserDefined,
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            seedContext.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = contextRowId,
                    ProjectId = projectId,
                    ContextId = "context-1",
                    Title = "Chat",
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow().AddMinutes(-2),
                    UpdateBy = "tester",
                    UpdateTime = TimeProvider.System.GetUtcNow().AddMinutes(-1),
                }
            );
            seedContext.ProjectConversationChatHistories.AddRange(
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = contextRowId,
                    TaskId = oldTaskId,
                    Status = TaskExecutionStatus.Succeeded,
                    CreateTime = TimeProvider.System.GetUtcNow().AddMinutes(-2),
                    UpdateTime = TimeProvider.System.GetUtcNow().AddMinutes(-2),
                },
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = contextRowId,
                    TaskId = latestTaskId,
                    Status = TaskExecutionStatus.Running,
                    CreateTime = TimeProvider.System.GetUtcNow().AddMinutes(-1),
                    UpdateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var result = await service.ResolveTaskAsync(
            new ExecutionTaskRequest(
                TaskId: null,
                ConversationId: contextRowId,
                ProjectId: projectId,
                ContextId: "context-1",
                Input: "resume",
                Resume: true,
                User: "tester"
            ),
            cancellationToken
        );

        Assert.Null(result.Error);
        Assert.NotNull(result.Task);
        Assert.Equal(latestTaskId, result.Task!.TaskId);
        Assert.Equal("context-1", result.Task.ContextId);
    }

    private static TaskAppService CreateService(AgwDbContext dbContext, IUnitOfWork? unitOfWork = null)
    {
        IProjectsDbContext persistence =
            unitOfWork == null ? dbContext : new DelegatingProjectsDbContext(dbContext, unitOfWork);
        var userInfo = new TestUserInfoService();
        var projectResolver = new ProjectResolver(persistence, userInfo);
        var taskExecutionAppService = new TaskExecutionAppService(
            persistence,
            projectResolver,
            TimeProvider.System,
            userInfo
        );

        return new TaskAppService(persistence, projectResolver, taskExecutionAppService, userInfo);
    }

    private static Project CreateProject(Guid projectId, string owner = "tester") =>
        new()
        {
            Id = projectId,
            Name = "Chat Project",
            Type = ProjectType.UserDefined,
            CreateBy = owner,
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static ProjectConversation CreateConversation(
        Guid conversationId,
        Guid projectId,
        string contextId,
        string owner = "tester"
    ) =>
        new()
        {
            Id = conversationId,
            ProjectId = projectId,
            ContextId = contextId,
            Title = "Chat",
            CreateBy = owner,
            CreateTime = TimeProvider.System.GetUtcNow(),
            UpdateBy = owner,
            UpdateTime = TimeProvider.System.GetUtcNow(),
        };

    private sealed class SqliteTestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private SqliteTestDatabase(SqliteConnection connection, DbContextOptions<AgwDbContext> options)
        {
            _connection = connection;
            Options = options;
        }

        public DbContextOptions<AgwDbContext> Options { get; }

        public static async Task<SqliteTestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var setupContext = new AgwDbContext(options);
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            return new SqliteTestDatabase(connection, options);
        }

        public AgwDbContext CreateContext() => new(Options);

        public async Task SeedAsync(CancellationToken cancellationToken, params object[] entities)
        {
            await using var seedContext = CreateContext();
            seedContext.AddRange(entities);
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed class ConcurrentConversationInsertUnitOfWork : IUnitOfWork
    {
        private readonly AgwDbContext _dbContext;
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly ProjectConversation _concurrentConversation;
        private bool _inserted;

        public ConcurrentConversationInsertUnitOfWork(
            AgwDbContext dbContext,
            DbContextOptions<AgwDbContext> options,
            ProjectConversation concurrentConversation
        )
        {
            _dbContext = dbContext;
            _options = options;
            _concurrentConversation = concurrentConversation;
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_inserted)
            {
                _inserted = true;
                await using var concurrentContext = new AgwDbContext(_options);
                concurrentContext.ProjectConversations.Add(_concurrentConversation);
                await concurrentContext.SaveChangesAsync(cancellationToken);
            }

            return await _dbContext.SaveChangesAsync(cancellationToken);
        }

        public async Task<bool> SaveEntitiesAsync(CancellationToken cancellationToken = default) =>
            await SaveChangesAsync(cancellationToken) > 0;

        public void Dispose() { }
    }

    private sealed class DelegatingProjectsDbContext : IProjectsDbContext
    {
        public Task<int> SaveConversationChangesAsync(
            Guid conversationId,
            int expectedGeneration,
            CancellationToken cancellationToken = default
        ) => _unitOfWork.SaveChangesAsync(cancellationToken);

        private readonly AgwDbContext _dbContext;
        private readonly IUnitOfWork _unitOfWork;

        public DelegatingProjectsDbContext(AgwDbContext dbContext, IUnitOfWork unitOfWork)
        {
            _dbContext = dbContext;
            _unitOfWork = unitOfWork;
        }

        public DbSet<Project> Projects => _dbContext.Projects;
        public DbSet<ProjectSkillRelation> ProjectSkillRelations => _dbContext.ProjectSkillRelations;
        public DbSet<ProjectMcpServerRelation> ProjectMcpToolServers => _dbContext.ProjectMcpToolServers;
        public DbSet<ProjectConnectionRelation> ProjectConnectionRelations => _dbContext.ProjectConnectionRelations;
        public DbSet<ProjectConversation> ProjectConversations => _dbContext.ProjectConversations;
        public DbSet<ProjectConversationChatHistory> ProjectConversationChatHistories =>
            _dbContext.ProjectConversationChatHistories;
        public DbSet<TaskSessionBinding> TaskSessionBindings => _dbContext.TaskSessionBindings;
        public DbSet<AgentUsage> AgentUsages => _dbContext.AgentUsages;

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
