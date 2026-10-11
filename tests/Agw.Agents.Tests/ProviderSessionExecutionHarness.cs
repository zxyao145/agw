using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using Agw.Agents.Execution.Agentflows.Turns;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Extensions;
using Agw.Files;
using Agw.Files.Abstracts;
using Agw.Infrastructure;
using Agw.Infrastructure.Data;
using Agw.Integrations.Extensions;
using Agw.Jobs;
using Agw.Projects;
using Agw.Projects.Application;
using Agw.Projects.Contracts.Execution;
using Agw.Providers;
using Agw.Settings;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Agw.Skills;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.CodexSdk;
using OpenAI.CodexSdk.MAF;
using PiAgentSdk;
using PiAgentSdk.MAF;

namespace Agw.Agents.Tests;

/// <summary>
/// 用全部模块的真实注册组装执行环境：真实 AgentRuntimeFactory、InProcessExecutionCoordinator、绑定持久化与 SQLite 数据库；External Agent 进程由 <see cref="FakeExternalAgentCli"/> 提供。
/// Assembles the execution environment from every module's real registrations: the real AgentRuntimeFactory, InProcessExecutionCoordinator, binding persistence and a SQLite database; the External Agent processes come from <see cref="FakeExternalAgentCli"/>.
/// </summary>
internal sealed class ProviderSessionExecutionHarness : IAsyncDisposable
{
    // Pi 的运行目录要求稳定的数字用户 ID。
    // Pi runtime directories require a stable numeric user ID.
    public const string UserId = "1001";
    public const string ContextId = "provider-session-chat";

    private readonly string _root;
    private readonly ServiceProvider _services;
    private readonly CancellationTokenSource _host = new();
    private AsyncServiceScope _connection;

    private ProviderSessionExecutionHarness(
        string root,
        ServiceProvider services,
        FakeExternalAgentCli cli,
        BindingCommandLog commands
    )
    {
        _root = root;
        _services = services;
        Cli = cli;
        Commands = commands;
        _connection = services.CreateAsyncScope();
    }

    public FakeExternalAgentCli Cli { get; }

    public BindingCommandLog Commands { get; }

    public Guid ProjectId { get; } = Guid.CreateVersion7();

    public Guid ConversationId { get; } = Guid.CreateVersion7();

    public Guid AgentId { get; } = Guid.CreateVersion7();

    public string AgentName { get; } = "provider-session-agent";

    public string DatabasePath => Path.Combine(_root, "agw.db");

    public InProcessExecutionCoordinator Coordinator { get; private set; } = null!;

    public InProcessTurnHost Host => Assert.IsType<InProcessTurnHost>(Coordinator.Host);

    public AgentRuntime Runtime => Assert.IsType<AgentRuntime>(Host.AgentRuntime);

    /// <summary>
    /// 执行连接 scope 中的绑定服务，与 Runtime 创建和复用检查使用同一个实例。
    /// The binding service of the execution connection scope, the same instance Runtime creation and reuse checks use.
    /// </summary>
    public ExternalProviderSessionBindings ProviderBindings =>
        _connection.ServiceProvider.GetRequiredService<ExternalProviderSessionBindings>();

    public AgentRuntimeFactory RuntimeFactory => _connection.ServiceProvider.GetRequiredService<AgentRuntimeFactory>();

    public Agent Definition =>
        new()
        {
            Id = AgentId,
            Name = AgentName,
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.Codex,
            CreateBy = UserId,
        };

    public AgentExecutionTask ExecutionTask =>
        new()
        {
            TaskId = Guid.CreateVersion7(),
            ProjectId = ProjectId,
            ProjectConversationId = ConversationId,
            ContextId = ContextId,
        };

    public Task<ExternalProviderSessionState?> ReadProviderSessionAsync() =>
        ProviderBindings.ReadStateAsync(Definition, ExecutionTask, ContextId, TestContext.Current.CancellationToken);

    /// <summary>
    /// 用真实的配置版本与给定的绑定状态构造一个可供复用检查的 Runtime。
    /// Builds a Runtime for reuse checks with the real configuration version and the given binding state.
    /// </summary>
    public async Task<AgentRuntime> CreateRuntimeAsync(AIAgent agent, ExternalProviderSessionState? providerSession)
    {
        var configuration = _connection.ServiceProvider.GetRequiredService<AgentRuntimeConfiguration>();
        return new AgentRuntime(
            NullLogger.Instance,
            agent,
            await agent.CreateSessionAsync(TestContext.Current.CancellationToken),
            ProjectId,
            ContextId,
            new AgentSessionStateScope(ConversationId, ProjectId, ContextId, AgentId, generation: 0),
            AgentType.External
        )
        {
            ConfigurationVersion = await configuration.ReadAsync(
                AgentId,
                ProjectId,
                TestContext.Current.CancellationToken
            ),
            ProviderSession = providerSession,
        };
    }

    /// <summary>
    /// 创建环境与一个使用替身命令行的 External Agent；调用方在同步代码中建立 <see cref="UserId"/> 的用户上下文。
    /// Creates the environment and an External Agent backed by the stand-in command line; callers establish the <see cref="UserId"/> user context in synchronous code.
    /// </summary>
    public static async Task<ProviderSessionExecutionHarness> CreateAsync(
        EngineKind kind,
        Func<IConversationExecutionGate, IConversationExecutionGate>? wrapGate = null
    )
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", $"provider-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var cli = new FakeExternalAgentCli(Path.Combine(root, "cli"));
        var commands = new BindingCommandLog(Path.Combine(root, "agw.db"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "sqlite",
                    ["Database:ConnectionString"] = $"Data Source={Path.Combine(root, "agw.db")};Pooling=False",
                    ["Execution:Provider"] = "InProcess",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(AgwDataPaths.Resolve(Path.Combine(root, "data"), root, Path.Combine(root, "logs")));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddDataProtection();
        services.AddHttpClient();
        services.AddScoped<IEntityAuditUserIdProvider, AuditUserIdProvider>();
        services
            .AddTools(configuration)
            .AddAgents(configuration)
            .AddAgentExecution(
                configuration,
                new Agw.Agents.Execution.DependencyInjection.RegistrationOptions(
                    AddExecutionTransport: false,
                    AddDistributedWorker: false,
                    AddTraceCollector: false
                )
            )
            .AddFiles(configuration)
            .AddInfrastructure(configuration)
            .AddJobs(configuration, new Agw.Jobs.DependencyInjection.RegistrationOptions(AddScheduler: false))
            .AddProviders(configuration)
            .AddSkills(configuration)
            .AddProjects(configuration)
            .AddAuth()
            .AddSettings()
            .AddIntegrations(configuration);

        var harness = new ProviderSessionExecutionHarness(root, services.BuildServiceProvider(), cli, commands);
        await harness.SeedAsync(kind);
        harness.Coordinator = harness.CreateCoordinator(wrapGate);
        return harness;
    }

    public static IDisposable EnterUser() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "Test"))
        );

    /// <summary>
    /// 以真实受理事务登记一个 Turn 后交给协调器；返回受理请求，调用方决定等待方式。
    /// Registers a turn through the real acceptance transaction and hands it to the coordinator; returns the accepted request so the caller decides how to wait.
    /// </summary>
    public async Task<Guid> StartTurnAsync(string text = "hello")
    {
        var acceptance = _connection.ServiceProvider.GetRequiredService<TurnAcceptanceService>();
        var accepted = await acceptance.AcceptAsync(
            new TurnAcceptanceRequest(
                UserId,
                TurnId: null,
                new ExecutionTarget(AgentId, AgentRuntimeType.Agent),
                ConversationId,
                TurnPersistenceTestKit.CreateInput(text),
                SettingCommandMapper.FromCommand(new SettingCommand(ProjectId, ConversationId)),
                Stream: false
            ),
            TestContext.Current.CancellationToken
        );
        try
        {
            var receipt = await Coordinator.StartAsync(accepted.Request, TestContext.Current.CancellationToken);
            Assert.True(receipt.Accepted);
        }
        catch (Exception exception)
        {
            await acceptance.ReportStartFailureAsync(accepted, exception);
            throw;
        }

        return accepted.Request.TurnId;
    }

    public async Task<ProviderSessionTurnResult> RunTurnAsync(string text = "hello")
    {
        var turnId = await StartTurnAsync(text);
        await WaitIdleAsync();
        return await ReadTurnAsync(turnId);
    }

    public Task WaitIdleAsync() =>
        Host.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    public async Task<ProviderSessionTurnResult> ReadTurnAsync(Guid turnId)
    {
        var turn = await QueryAsync(context =>
            context
                .ProjectConversationTurns.AsNoTracking()
                .SingleAsync(item => item.Id == turnId, TestContext.Current.CancellationToken)
        );
        var broadcast = _services.GetRequiredService<TurnBroadcastRegistry>().Find(turnId, UserId);
        var errors =
            broadcast
                ?.ReadAfter(0, out _)
                .SelectMany(entry => entry.Message.Contents.OfType<AgwErrorContent>())
                .Select(content => content.Content ?? string.Empty)
                .ToList()
            ?? [];
        return new ProviderSessionTurnResult(turn.Status, turn.ErrorCode, errors);
    }

    public async Task<List<ProjectConversationBinding>> ReadBindingsAsync()
    {
        var bindings = await QueryAsync(context =>
            context
                .ProjectConversationBindings.AsNoTracking()
                .Where(binding => binding.ProjectConversationId == ConversationId)
                .ToListAsync(TestContext.Current.CancellationToken)
        );
        return bindings.OrderBy(binding => binding.CreateTime).ThenBy(binding => binding.Id).ToList();
    }

    /// <summary>
    /// 以给定的 provider session ID 保存绑定组的生效记录，与 SDK 回调使用的应用服务相同。
    /// Saves the binding group's active record with the given provider session ID through the application service SDK callbacks use.
    /// </summary>
    public async Task SaveBindingAsync(string providerSessionId)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<ITaskSessionBindingService>()
            .UpsertAsync(
                ProjectId,
                ContextId,
                AgentId,
                AgentName,
                providerSessionId,
                UserId,
                TestContext.Current.CancellationToken
            );
    }

    /// <summary>
    /// 在独立 scope 中通过归档接口使用的应用服务归档记录，与 HTTP 请求所在的 scope 一致。
    /// Archives a record through the application service behind the archive endpoint, in its own scope like an HTTP request.
    /// </summary>
    public async Task ArchiveAsync(Guid bindingId)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<ITaskSessionBindingService>()
            .ArchiveAsync(ProjectId, ConversationId, bindingId, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 执行一条真实的 SQL 语句，用来制造数据库层的失败并在之后恢复。
    /// Runs a real SQL statement, used to cause database-level failures and to restore afterwards.
    /// </summary>
    public Task ExecuteSqlAsync(string sql) =>
        QueryAsync(context => context.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken));

    private async Task<T> QueryAsync<T>(Func<AgwDbContext, Task<T>> query)
    {
        await using var scope = _services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AgwDbContext>());
    }

    private InProcessExecutionCoordinator CreateCoordinator(
        Func<IConversationExecutionGate, IConversationExecutionGate>? wrapGate
    )
    {
        var services = _connection.ServiceProvider;
        var gate = services.GetRequiredService<IConversationExecutionGate>();
        return new InProcessExecutionCoordinatorFactory(
            services.GetRequiredService<IAgentRuntimeFactory>(),
            services.GetRequiredService<AgentTurnExecutor>(),
            services.GetRequiredService<AgentflowTurnExecutor>(),
            services.GetRequiredService<ExecutionContextFactory>(),
            services.GetRequiredService<IAgwFileSystemResolver>(),
            services.GetRequiredService<TurnBroadcastRegistry>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            wrapGate == null ? gate : wrapGate(gate)
        ).Create(_host.Token);
    }

    private async Task SeedAsync(EngineKind kind)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var now = TimeProvider.System.GetUtcNow();
        context.Projects.Add(
            new Project
            {
                Id = ProjectId,
                Name = "provider-sessions",
                Type = ProjectType.UserDefined,
                Workspace = workspace,
                CreateBy = UserId,
                CreateTime = now,
            }
        );
        context.ProjectConversations.Add(
            new ProjectConversation
            {
                Id = ConversationId,
                ProjectId = ProjectId,
                ContextId = ContextId,
                Title = "Provider sessions",
                CreateBy = UserId,
                CreateTime = now,
            }
        );
        context.Agents.Add(
            new Agent
            {
                Id = AgentId,
                Name = AgentName,
                DisplayName = "Provider Session Agent",
                Type = AgentType.External,
                ExternalAgentKind = kind,
                Enable = true,
                Extra = CreateExtra(kind),
                CreateBy = UserId,
                CreateTime = now,
            }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private string CreateExtra(EngineKind kind) =>
        kind switch
        {
            EngineKind.Codex => JsonUtil.Serialize(
                new CodexAIAgentOptions
                {
                    CodexOptions = new CodexOptions { CodexPathOverride = Cli.CodexPath },
                    ThreadOptions = new ThreadOptions { SkipGitRepoCheck = true },
                }
            ),
            EngineKind.Pi => JsonUtil.Serialize(
                new PiAgentAIAgentOptions { GlobalOptions = new PiAgentOptions { PiPathOverride = Cli.PiPath } }
            ),
            _ => throw new NotSupportedException(kind.ToString()),
        };

    public async ValueTask DisposeAsync()
    {
        await Coordinator.ReleaseAsync();
        await _host.CancelAsync();
        await _connection.DisposeAsync();
        await _services.DisposeAsync();
        _host.Dispose();
        Commands.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class AuditUserIdProvider : IEntityAuditUserIdProvider
    {
        public string GetUserId() => UserInfoUtil.UserId ?? UserId;
    }
}

internal sealed record ProviderSessionTurnResult(
    ProjectConversationTurnStatus Status,
    string? ErrorCode,
    IReadOnlyList<string> Errors
);

/// <summary>
/// 通过 EF 的诊断事件记录本测试数据库执行过的 SQL 命令，用来核对实际进入数据库的绑定读取与写入次数。EF 在多个服务容器之间复用内部服务，因此按数据库文件区分命令来源。
/// Records the SQL commands run against this test database through EF diagnostic events, used to verify how many binding loads and writes actually reached the database. EF reuses its internal services across service containers, so commands are attributed by database file.
/// </summary>
internal sealed class BindingCommandLog
    : IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>,
        IDisposable
{
    private readonly string _dataSource;
    private readonly ConcurrentDictionary<Guid, string> _commands = new();
    private readonly ConcurrentBag<IDisposable> _subscriptions = [];
    private readonly IDisposable _listeners;

    public BindingCommandLog(string dataSource)
    {
        _dataSource = dataSource;
        _listeners = DiagnosticListener.AllListeners.Subscribe(this);
    }

    /// <summary>
    /// 绑定表的 INSERT 与 UPDATE，包括执行失败的命令。
    /// INSERT and UPDATE commands on the binding table, including failed ones.
    /// </summary>
    public IReadOnlyList<string> BindingWrites =>
        _commands
            .Values.Where(command =>
                command.Contains("INSERT INTO \"project_conversation_binding\"", StringComparison.Ordinal)
                || command.Contains("UPDATE \"project_conversation_binding\"", StringComparison.Ordinal)
            )
            .ToList();

    /// <summary>
    /// 协调器在锁内加载对话与目标绑定组的查询，每次保存或归档操作执行一次。
    /// The coordinator's query loading the conversation with the target binding group under the locks, run once per save or archive operation.
    /// </summary>
    public IReadOnlyList<string> BindingLoads =>
        _commands
            .Values.Where(command =>
                command.Contains("LEFT JOIN", StringComparison.Ordinal)
                && command.Contains("FROM \"project_conversation_binding\"", StringComparison.Ordinal)
            )
            .ToList();

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == DbLoggerCategory.Name)
        {
            _subscriptions.Add(listener.Subscribe(this));
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        if (
            value.Value is CommandExecutedEventData or CommandErrorEventData
            && value.Value is CommandEndEventData data
            && string.Equals(data.Command.Connection?.DataSource, _dataSource, StringComparison.Ordinal)
        )
        {
            _commands.TryAdd(data.CommandId, data.Command.CommandText);
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted() { }

    void IObserver<DiagnosticListener>.OnError(Exception error) { }

    void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }

    public void Dispose()
    {
        _listeners.Dispose();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }
}
