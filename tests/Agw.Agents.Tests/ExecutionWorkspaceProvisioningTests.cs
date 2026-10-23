using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Extensions;
using Agw.Files;
using Agw.Infrastructure;
using Agw.Infrastructure.Data;
using Agw.Integrations.Extensions;
using Agw.Jobs;
using Agw.Projects;
using Agw.Projects.Application;
using Agw.Providers;
using Agw.Settings;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Agw.Skills;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenAI.CodexSdk;
using OpenAI.CodexSdk.MAF;

namespace Agw.Agents.Tests;

/// <summary>
/// 执行入口在使用目录前创建缺失的主目录与附加目录：InProcess 的首个与后续 Turn，以及外部 Agent 的启动与继续执行。外部 Agent 进程使用仓库中
/// 的命令行替身，替身记录启动后工作目录的物理路径。
/// Execution entries create missing primary and additional directories before using them: the first and later InProcess
/// turns, and external Agent start and continuation. External Agent processes use the repository's command-line
/// stand-ins, which record the physical path of their working directory after start.
/// </summary>
public sealed partial class ExecutionWorkspaceProvisioningTests : IDisposable
{
    private readonly IDisposable _user = ProviderSessionExecutionHarness.EnterUser();

    public static bool CanRunFakeCli => !OperatingSystem.IsWindows();

    public void Dispose() => _user.Dispose();

    [Theory(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    [InlineData(EngineKind.Codex)]
    [InlineData(EngineKind.Pi)]
    public async Task StartAsync_ExternalAgentDirectoriesMissing_CreatesDirectoriesBeforeStart(EngineKind kind)
    {
        // Arrange
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(kind);
        var (workspace, additional) = await harness.UseProjectDirectoriesAsync();
        Directory.Delete(workspace);
        Directory.Delete(additional);

        // Act
        var turn = await harness.RunTurnAsync();

        // Assert
        Assert.Equal(ProjectConversationTurnStatus.Completed, turn.Status);
        Assert.True(Directory.Exists(workspace));
        Assert.True(Directory.Exists(additional));
        var invocation = Assert.Single(
            kind == EngineKind.Pi ? harness.Cli.PiInvocations : harness.Cli.CodexInvocations
        );
        Assert.Equal(workspace, invocation.WorkingDirectory);
    }

    [Fact(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    public async Task StartAsync_PiDirectoriesMovedBetweenTurns_ContinuesSessionInRecreatedDirectories()
    {
        // Arrange
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Pi);
        var (workspace, additional) = await harness.UseProjectDirectoriesAsync();
        var first = await harness.RunTurnAsync();
        var firstRuntime = harness.Runtime;
        Directory.Move(workspace, workspace + "-backup");
        Directory.Move(additional, additional + "-backup");

        // Act
        var second = await harness.RunTurnAsync();

        // Assert
        Assert.Equal(ProjectConversationTurnStatus.Completed, first.Status);
        Assert.Equal(ProjectConversationTurnStatus.Completed, second.Status);
        Assert.True(firstRuntime.IsDisposed);
        Assert.NotSame(firstRuntime, harness.Runtime);
        Assert.True(Directory.Exists(workspace));
        Assert.True(Directory.Exists(additional));
        var invocations = harness.Cli.PiInvocations;
        Assert.Equal(2, invocations.Count);
        Assert.Equal(invocations[0].SessionId, invocations[1].Resumed);
        Assert.All(invocations, invocation => Assert.Equal(workspace, invocation.WorkingDirectory));
    }

    [Fact(SkipUnless = nameof(CanRunFakeCli), Skip = "The command-line stand-ins are Node scripts with a shebang.")]
    public async Task StartAsync_DirectoriesUnchangedBetweenTurns_ReusesRuntime()
    {
        // Arrange
        await using var harness = await ProviderSessionExecutionHarness.CreateAsync(EngineKind.Pi);
        await harness.UseProjectDirectoriesAsync();
        await harness.RunTurnAsync();
        var firstRuntime = harness.Runtime;

        // Act
        var second = await harness.RunTurnAsync();

        // Assert
        Assert.Equal(ProjectConversationTurnStatus.Completed, second.Status);
        Assert.Same(firstRuntime, harness.Runtime);
        Assert.Single(harness.Cli.PiInvocations);
    }

    [Fact(
        SkipUnless = nameof(PostgresEnabled),
        Skip = "Requires an isolated PostgreSQL server with CREATE DATABASE permission."
    )]
    public async Task DurableSegments_DirectoriesMissingBeforeEachSegment_CreateCapturedDirectories()
    {
        // Arrange
        await using var fixture = await DurableWorkspaceFixture.CreateAsync();
        var (workspace, additional) = await fixture.SeedAsync();
        Directory.Delete(workspace);
        Directory.Delete(additional);
        var turnId = await fixture.AcceptAsync();

        // Act
        await fixture.RunSegmentAsync(turnId);
        var waiting = await fixture.ReadAsync(turnId);
        Assert.True(
            waiting.Status == DurableExecutionStatus.WaitingForHuman,
            $"{waiting.Status}: {waiting.ErrorMessage}"
        );
        var workspaceCreatedForFirstSegment = Directory.Exists(workspace);
        var additionalCreatedForFirstSegment = Directory.Exists(additional);
        Directory.Move(workspace, workspace + "-backup");
        Directory.Move(additional, additional + "-backup");
        await fixture.ApproveAsync(turnId, Assert.Single(waiting.GetUnansweredInteractions()).InteractionId);
        await fixture.RunSegmentAsync(turnId);
        var completed = await fixture.ReadAsync(turnId);

        // Assert
        Assert.Equal(DurableExecutionStatus.WaitingForHuman, waiting.Status);
        Assert.True(workspaceCreatedForFirstSegment);
        Assert.True(additionalCreatedForFirstSegment);
        Assert.True(
            completed.Status == DurableExecutionStatus.Completed,
            $"{completed.Status}: {completed.ErrorMessage}"
        );
        Assert.True(Directory.Exists(workspace));
        Assert.True(Directory.Exists(additional));
        Assert.Equal(workspace, Assert.Single(fixture.Cli.CodexInvocations).WorkingDirectory);
    }

    public static bool PostgresEnabled => DurableTurnUpgradeTests.PostgresEnabled;

    /// <summary>
    /// 在独立 PostgreSQL 测试库上以生产 DI 组合 Distributed 执行，Agentflow 为 Input → HumanGate → Output，不需要模型服务。
    /// Composes Distributed execution with production DI on a dedicated PostgreSQL test database; the Agentflow is
    /// Input → HumanGate → Output and needs no model service.
    /// </summary>
    private sealed class DurableWorkspaceFixture : IAsyncDisposable
    {
        private const string UserId = ProviderSessionExecutionHarness.UserId;
        private const string ContextId = "durable-workspace";

        private readonly string _root;
        private readonly string _adminConnectionString;
        private readonly string _database;
        private readonly ServiceProvider _services;
        private readonly IHost _host;
        private readonly DurableSegmentScheduler _scheduler;

        private DurableWorkspaceFixture(
            string root,
            string adminConnectionString,
            string database,
            ServiceProvider services,
            IHost host
        )
        {
            _root = root;
            _adminConnectionString = adminConnectionString;
            _database = database;
            _services = services;
            _host = host;
            Cli = new FakeExternalAgentCli(Path.Combine(root, "cli"));
            _scheduler = new DurableSegmentScheduler(
                services,
                host.Services.GetRequiredService<IHostApplicationLifetime>(),
                NullLogger<DurableSegmentScheduler>.Instance,
                services.GetRequiredService<IDurableExecutionLeases>(),
                services.GetRequiredService<DurableWorkerIdentity>(),
                services.GetRequiredService<IOptions<ExecutionRuntimeOptions>>()
            );
        }

        public FakeExternalAgentCli Cli { get; }

        private Guid ProjectId { get; } = Guid.CreateVersion7();

        private Guid ConversationId { get; } = Guid.CreateVersion7();

        private Guid AgentflowId { get; } = Guid.CreateVersion7();

        private Guid AgentId { get; } = Guid.CreateVersion7();

        public static async Task<DurableWorkspaceFixture> CreateAsync()
        {
            var token = TestContext.Current.CancellationToken;
            var root = Path.Combine(
                AppContext.BaseDirectory,
                ".scratch",
                "durable-workspace",
                Guid.CreateVersion7().ToString("N")
            );
            Directory.CreateDirectory(root);
            var admin = new NpgsqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING")
            )
            {
                Pooling = false,
            };
            var database = $"agw_workspace_test_{Guid.NewGuid():N}";
            await using (var connection = new NpgsqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
                await create.ExecuteNonQueryAsync(token);
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Database:Provider"] = "postgres",
                        ["Database:ConnectionString"] = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
                        {
                            Database = database,
                        }.ConnectionString,
                        ["DistributedLock:Provider"] = "postgres",
                        ["Execution:Provider"] = "Distributed",
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
            var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<AgwDbContext>().Database.MigrateAsync(token);
            }

            return new DurableWorkspaceFixture(
                root,
                admin.ConnectionString,
                database,
                provider,
                Host.CreateApplicationBuilder().Build()
            );
        }

        /// <summary>
        /// 通过项目应用服务创建带主目录和一个附加目录的 Project，再写入对话与 Agentflow；返回两个目录的绝对路径。
        /// Creates a Project with a primary and one additional directory through the Project application service, then
        /// writes the conversation and the Agentflow; returns the absolute paths of both directories.
        /// </summary>
        public async Task<(string Workspace, string Additional)> SeedAsync()
        {
            var token = TestContext.Current.CancellationToken;
            var workspace = Path.Combine(_root, "workspace");
            var additional = Path.Combine(_root, "additional");
            await using var scope = _services.CreateAsyncScope();
            var created = await scope
                .ServiceProvider.GetRequiredService<IProjectAppService>()
                .CreateAsync(
                    new Project
                    {
                        Id = ProjectId,
                        Name = "durable-workspace",
                        Type = ProjectType.UserDefined,
                        Workspace = workspace,
                        AdditionalDirectories = [new ProjectDirectory { Path = additional }],
                    },
                    null,
                    null,
                    null
                );
            Assert.NotNull(created);
            var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            var now = TimeProvider.System.GetUtcNow();
            context.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = ConversationId,
                    ProjectId = ProjectId,
                    ContextId = ContextId,
                    Title = "Durable workspace",
                    CreateBy = UserId,
                    CreateTime = now,
                }
            );
            context.Agentflows.Add(
                new Agentflow
                {
                    Id = AgentflowId,
                    Name = "durable-workspace-flow",
                    CreateBy = UserId,
                    CreateTime = now,
                    Nodes =
                    [
                        new AgentflowNode
                        {
                            NodeId = "input",
                            Kind = AgentflowNodeKind.Input,
                            Name = "Input",
                            CreateBy = UserId,
                            CreateTime = now,
                        },
                        new AgentflowNode
                        {
                            NodeId = "gate",
                            Kind = AgentflowNodeKind.HumanGate,
                            Name = "Gate",
                            CreateBy = UserId,
                            CreateTime = now,
                        },
                        new AgentflowNode
                        {
                            NodeId = "agent",
                            Kind = AgentflowNodeKind.Agent,
                            RelateId = AgentId,
                            Name = "Agent",
                            CreateBy = UserId,
                            CreateTime = now,
                        },
                        new AgentflowNode
                        {
                            NodeId = "output",
                            Kind = AgentflowNodeKind.Output,
                            Name = "Output",
                            CreateBy = UserId,
                            CreateTime = now,
                        },
                    ],
                    Edges =
                    [
                        new AgentflowEdge
                        {
                            EdgeId = "input-gate",
                            SourceNodeId = "input",
                            TargetNodeId = "gate",
                            CreateBy = UserId,
                            CreateTime = now,
                        },
                        new AgentflowEdge
                        {
                            EdgeId = "gate-agent",
                            SourceNodeId = "gate",
                            TargetNodeId = "agent",
                            CreateBy = UserId,
                            CreateTime = now,
                        },
                        new AgentflowEdge
                        {
                            EdgeId = "agent-output",
                            SourceNodeId = "agent",
                            TargetNodeId = "output",
                            CreateBy = UserId,
                            CreateTime = now,
                        },
                    ],
                }
            );
            context.Agents.Add(
                new Agent
                {
                    Id = AgentId,
                    Name = "durable-workspace-agent",
                    DisplayName = "Durable Workspace Agent",
                    Type = AgentType.External,
                    ExternalAgentKind = EngineKind.Codex,
                    Enable = true,
                    Extra = JsonUtil.Serialize(
                        new CodexAIAgentOptions
                        {
                            CodexOptions = new CodexOptions { CodexPathOverride = Cli.CodexPath },
                            ThreadOptions = new ThreadOptions { SkipGitRepoCheck = true },
                        }
                    ),
                    CreateBy = UserId,
                    CreateTime = now,
                }
            );
            await context.SaveChangesAsync(token);
            return (workspace, additional);
        }

        /// <summary>
        /// 通过真实受理事务登记一个 Durable Turn，并交给 Distributed 协调器排队。
        /// Registers a durable turn through the real acceptance transaction and queues it on the Distributed coordinator.
        /// </summary>
        public async Task<Guid> AcceptAsync()
        {
            var token = TestContext.Current.CancellationToken;
            await using var scope = _services.CreateAsyncScope();
            var accepted = await scope
                .ServiceProvider.GetRequiredService<TurnAcceptanceService>()
                .AcceptAsync(
                    new TurnAcceptanceRequest(
                        UserId,
                        TurnId: null,
                        new ExecutionTarget(AgentflowId, AgentRuntimeType.Agentflow),
                        ConversationId,
                        TurnPersistenceTestKit.CreateInput("continue"),
                        SettingCommandMapper.FromCommand(new SettingCommand(ProjectId, ConversationId)),
                        Stream: true
                    ),
                    token
                );
            var receipt = await _services
                .GetRequiredService<DurableExecutionCoordinator>()
                .StartAsync(accepted.Request, token);
            Assert.True(receipt.Accepted);
            return accepted.Request.TurnId;
        }

        public async Task RunSegmentAsync(Guid turnId)
        {
            Assert.True(await _scheduler.TryStartAsync(turnId, TestContext.Current.CancellationToken));
            await _scheduler.WaitAllAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        public async Task<DurableExecutionSnapshot> ReadAsync(Guid turnId)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope
                .ServiceProvider.GetRequiredService<DurableExecutionStore>()
                .GetAsync(turnId, TestContext.Current.CancellationToken);
        }

        public Task ApproveAsync(Guid turnId, string interactionId) =>
            _services
                .GetRequiredService<DurableExecutionCoordinator>()
                .SubmitHumanResponseAsync(
                    new SubmitDurableHumanResponseRequest(
                        turnId,
                        new WorkflowGateDecision { InteractionId = interactionId, Approved = true }
                    ),
                    UserId,
                    TestContext.Current.CancellationToken
                );

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            _host.Dispose();
            await using (var connection = new NpgsqlConnection(_adminConnectionString))
            {
                await connection.OpenAsync(CancellationToken.None);
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{_database}\" WITH (FORCE)", connection);
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
            }

            Directory.Delete(_root, recursive: true);
        }

        private sealed class AuditUserIdProvider : IEntityAuditUserIdProvider
        {
            public string GetUserId() => UserInfoUtil.UserId ?? UserId;
        }
    }
}
