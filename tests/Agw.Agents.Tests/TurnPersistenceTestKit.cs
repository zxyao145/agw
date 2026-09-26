using System.Security.Claims;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Messaging.Durable;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.History;
using Agw.Projects.Contracts.Runtime;
using Agw.Projects.Infrastructure;
using Agw.Shared.Configuration;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agw.Agents.Tests;

/// <summary>
/// Turn 受理与 Durable 测试共用的数据库与服务：真实的 AgwDbContext、Projects 的 Turn 与历史存储、Infrastructure 的受理事务与租约，以及 Durable 协调器。
/// 默认使用 SQLite 文件数据库；PostgreSQL 版本在隔离的测试服务器上创建独立数据库。
/// The database and services shared by turn acceptance and durable tests: the real AgwDbContext, the Projects turn and history stores, the Infrastructure acceptance transaction and leases, and the durable coordinator.
/// SQLite file databases are the default; the PostgreSQL variant creates its own database on an isolated test server.
/// </summary>
internal sealed class TurnPersistenceTestKit : IAsyncDisposable
{
    public const string UserId = "user-id";
    public const string ContextId = "context-1";

    private readonly List<AsyncServiceScope> _scopes = [];
    private readonly Func<ValueTask> _dropDatabase;

    private TurnPersistenceTestKit(
        DbContextOptions<AgwDbContext> dbOptions,
        Func<ValueTask> dropDatabase,
        TimeProvider clock,
        ExecutionRuntimeOptions executionOptions,
        Action<IServiceCollection>? configure
    )
    {
        DbOptions = dbOptions;
        _dropDatabase = dropDatabase;
        Clock = clock;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock);
        services.AddSingleton<IApplicationLock>(Locks);
        services.AddScoped(_ => new AgwDbContext(DbOptions));
        services.AddScoped<IAgentsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddScoped<IProjectsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddScoped<IDurableExecutionScopeMaintenance, DurableExecutionScopeMaintenance>();
        services.AddScoped<IDurableExecutionEventSequence, DurableExecutionEventSequence>();
        services.AddScoped<IConversationTurnStore, ConversationTurnStore>();
        services.AddScoped<ITurnAcceptanceWriter, TurnAcceptanceWriter>();
        services.AddSingleton<IDurableExecutionLeases, DurableExecutionLeases>();
        services.AddSingleton(Options.Create(executionOptions));
        services.AddSingleton<TurnBroadcastRegistry>();
        services.AddSingleton(provider => new ConversationHistoryStore(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Locks,
            NullLogger<ConversationHistoryStore>.Instance,
            clock
        ));
        services.AddSingleton<IConversationHistoryStore>(provider =>
            provider.GetRequiredService<ConversationHistoryStore>()
        );
        services.AddScoped<DurableExecutionStore>();
        services.AddSingleton<DurableWorkerIdentity>();
        services.AddSingleton<DurableExecutionEventLog>();
        services.AddSingleton<DurableExecutionCoordinator>();
        configure?.Invoke(services);
        Services = services.BuildServiceProvider();
    }

    public TimeProvider Clock { get; }

    public DbContextOptions<AgwDbContext> DbOptions { get; }

    public InMemoryApplicationLock Locks { get; } = new();

    public ServiceProvider Services { get; }

    public TurnBroadcastRegistry Broadcasts => Services.GetRequiredService<TurnBroadcastRegistry>();

    public DurableExecutionCoordinator Coordinator => Services.GetRequiredService<DurableExecutionCoordinator>();

    public IDurableExecutionLeases Leases => Services.GetRequiredService<IDurableExecutionLeases>();

    public static async Task<TurnPersistenceTestKit> CreateAsync(
        TimeProvider? clock = null,
        ExecutionRuntimeOptions? executionOptions = null,
        Action<IServiceCollection>? configure = null,
        params IInterceptor[] interceptors
    )
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-databases", $"agw-turns-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptors)
            .Options;
        var kit = new TurnPersistenceTestKit(
            options,
            () =>
            {
                File.Delete(path);
                return ValueTask.CompletedTask;
            },
            clock ?? TimeProvider.System,
            executionOptions ?? new ExecutionRuntimeOptions(),
            configure
        );
        await using var context = kit.CreateContext();
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return kit;
    }

    /// <summary>
    /// 在隔离的 PostgreSQL 测试服务器上创建独立数据库并执行真实迁移；释放时删除该数据库。数据库时间取 PostgreSQL 的 now()。
    /// Creates a dedicated database on an isolated PostgreSQL test server and applies the real migrations; disposal drops that database. Database time comes from PostgreSQL now().
    /// </summary>
    public static async Task<TurnPersistenceTestKit> CreatePostgresAsync(
        string adminConnectionString,
        ExecutionRuntimeOptions? executionOptions = null
    )
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new NpgsqlConnectionStringBuilder(adminConnectionString) { Pooling = false };
        var adminSettings = settings.ConnectionString;
        var database = $"agw_turn_test_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(adminSettings))
        {
            await admin.OpenAsync(token);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(token);
        }
        settings.Database = database;
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        AgwDbContextOptionsConfigurator.Configure(options, DatabaseProvider.Postgres, settings.ConnectionString);
        var kit = new TurnPersistenceTestKit(
            options.Options,
            async () =>
            {
                await using var admin = new NpgsqlConnection(adminSettings);
                await admin.OpenAsync(CancellationToken.None);
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
            },
            TimeProvider.System,
            executionOptions ?? new ExecutionRuntimeOptions(),
            configure: null
        );
        await using var context = kit.CreateContext();
        await context.Database.MigrateAsync(token);
        return kit;
    }

    public AgwDbContext CreateContext() => new(DbOptions);

    /// <summary>
    /// 在新的作用域中解析服务；作用域随测试环境一起释放。
    /// Resolves a service in a new scope that is disposed with the kit.
    /// </summary>
    public T ResolveScoped<T>()
        where T : notnull
    {
        var scope = Services.CreateAsyncScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    public static IDisposable EnterUser(string userId = UserId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Test"))
        );

    /// <summary>
    /// 写入一个属于用户的项目与对话，返回对应的项目任务。
    /// Writes a project and conversation owned by the user and returns the matching project task.
    /// </summary>
    public async Task<AgentExecutionTask> SeedConversationAsync(string userId = UserId, string contextId = ContextId)
    {
        var task = new AgentExecutionTask
        {
            TaskId = Guid.CreateVersion7(),
            ProjectConversationId = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            ContextId = contextId,
            Title = "Turn test",
            CreateTime = Clock.GetUtcNow(),
        };
        await SeedConversationAsync(task, userId);
        return task;
    }

    /// <summary>
    /// 写入测试已有任务对应的项目与对话；已经存在的行保持不变。
    /// Writes the project and conversation of a task a test already has; existing rows stay unchanged.
    /// </summary>
    public async Task SeedConversationAsync(AgentExecutionTask task, string userId = UserId)
    {
        await using var context = CreateContext();
        if (!await context.Projects.IgnoreQueryFilters().AnyAsync(project => project.Id == task.ProjectId))
            context.Projects.Add(
                new Project
                {
                    Id = task.ProjectId,
                    Name = $"Test-{task.ProjectId:N}",
                    Workspace = AppContext.BaseDirectory,
                    CreateBy = userId,
                }
            );
        if (
            !await context
                .ProjectConversations.IgnoreQueryFilters()
                .AnyAsync(conversation => conversation.Id == task.ProjectConversationId)
        )
            context.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = task.ProjectConversationId,
                    ProjectId = task.ProjectId,
                    ContextId = task.ContextId,
                    Generation = task.Generation,
                    CreateBy = userId,
                }
            );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 把对话的 Generation 改为指定值，与另一个进程提交的重置一致。
    /// Sets the conversation generation, matching a reset committed by another process.
    /// </summary>
    public async Task SetGenerationAsync(Guid conversationId, int generation)
    {
        await using var context = CreateContext();
        await context
            .ProjectConversations.IgnoreQueryFilters()
            .Where(conversation => conversation.Id == conversationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(conversation => conversation.Generation, generation),
                TestContext.Current.CancellationToken
            );
    }

    /// <summary>
    /// 用真实的受理事务创建受理服务；Durable 协调器为空时是进程内受理。
    /// Creates an acceptance service over the real acceptance transaction; a null durable coordinator means in-process acceptance.
    /// </summary>
    public TurnAcceptanceService CreateAcceptance(
        IProjectTaskFacade? projectTasks,
        IProjectRuntimeFacade projects,
        DurableExecutionCoordinator? durable = null,
        ExecutionPermissionService? permissions = null
    )
    {
        var scope = Services.CreateAsyncScope();
        _scopes.Add(scope);
        return new TurnAcceptanceService(
            projectTasks!,
            projects,
            scope.ServiceProvider.GetRequiredService<ITurnAcceptanceWriter>(),
            scope.ServiceProvider.GetRequiredService<IConversationTurnStore>(),
            Broadcasts,
            Clock,
            permissions,
            durable
        );
    }

    /// <summary>
    /// 为直接交给协调器的请求写入对话、Turn 行与本 Turn 的广播，与受理事务提交后的状态一致；请求不带输入行。
    /// Writes the conversation, turn row and turn broadcast for a request handed directly to a coordinator, matching the state after the acceptance transaction; the request carries no input row.
    /// </summary>
    public async Task<ExecutionRequest> RegisterTurnAsync(ExecutionRequest request)
    {
        await SeedConversationAsync(request.Task, request.UserId);
        using var user = EnterUser(request.UserId);
        await using var scope = Services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<IConversationTurnStore>()
            .AcceptAsync(
                new AcceptConversationTurnRequest
                {
                    TurnId = request.TurnId,
                    ProjectId = request.Task.ProjectId,
                    ContextId = request.Task.ContextId,
                    ConversationId = request.Task.ProjectConversationId,
                    Generation = request.Task.Generation,
                    TaskId = request.Task.TaskId,
                    TargetId = request.Target.AgentId,
                    TargetType =
                        request.Target.AgentType == AgentRuntimeType.Agentflow
                            ? ConversationTurnTargetType.Agentflow
                            : ConversationTurnTargetType.Agent,
                },
                TestContext.Current.CancellationToken
            );
        Broadcasts.GetOrCreate(request.TurnId, request.UserId);
        return request with { InputMessageId = null };
    }

    public static TurnEnvelope CreateEnvelope(Guid turnId, AgentExecutionTask task, ExecutionTarget target) =>
        new(turnId, task.ProjectConversationId, target.AgentId, target.AgentType, turnId.ToString("D"));

    public static AgwUserInput CreateInput(string content) =>
        new()
        {
            MessageId = Guid.CreateVersion7().ToString("D"),
            Contents = [new AgwTextContent { Content = content }],
        };

    public static ExecutionSettings CreateSettings(Guid projectId, string contextId = ContextId) =>
        SettingCommandMapper.FromCommand(new SettingCommand(projectId, contextId: contextId));

    public async Task<ProjectConversationTurn> ReadTurnAsync(Guid turnId)
    {
        await using var context = CreateContext();
        return await context
            .ProjectConversationTurns.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(turn => turn.Id == turnId, TestContext.Current.CancellationToken);
    }

    public async Task<DurableExecutionRecord> ReadExecutionAsync(Guid executionId)
    {
        await using var context = CreateContext();
        return await context
            .DurableExecutions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(execution => execution.Id == executionId, TestContext.Current.CancellationToken);
    }

    public async Task<List<DurableExecutionEventRecord>> ReadEventsAsync(Guid turnId)
    {
        await using var context = CreateContext();
        return await context
            .DurableExecutionEvents.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entry => entry.TurnId == turnId)
            .OrderBy(entry => entry.TurnSequence)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    public async Task<List<ProjectConversationChatHistory>> ReadHistoryAsync(Guid conversationId)
    {
        await using var context = CreateContext();
        return await context
            .ProjectConversationChatHistories.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(row => row.ConversationId == conversationId && row.ConversationSequence != null)
            .OrderBy(row => row.ConversationSequence)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
            await scope.DisposeAsync();
        await Services.DisposeAsync();
        await _dropDatabase();
    }
}
