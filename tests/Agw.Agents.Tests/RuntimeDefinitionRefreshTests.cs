using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agentflows.Turns;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Files.Abstracts;
using Agw.Files.Infrastructure.Storage;
using Agw.Infrastructure.Data;
using Agw.Projects.Contracts.Runtime;
using Agw.Providers.Contracts;
using Agw.Providers.Contracts.References;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class RuntimeDefinitionRefreshTests
{
    [Theory]
    [InlineData(EngineKind.ClaudeCode)]
    [InlineData(EngineKind.Codex)]
    [InlineData(EngineKind.Pi)]
    public async Task StartAsync_DirectoriesChangeDuringTurn_FreezesActiveAndChildContextThenRebuilds(EngineKind kind)
    {
        await using var fixture = new Fixture(kind);
        await fixture.InitializeAsync();
        var extra = Directory.CreateTempSubdirectory("agw-runtime-directory-");
        try
        {
            var projectId = fixture.Request.Task.ProjectId;
            var original = ProjectWorkspacePaths.CreateSnapshot(
                projectId,
                Path.GetTempPath(),
                [new ProjectWorkspaceDirectory(Guid.CreateVersion7(), extra.FullName)]
            );
            fixture.Request = fixture.Request with { WorkspaceSnapshot = original };
            fixture.Service.HoldTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await fixture.StartAsync();
            var host = fixture.Host;
            var active = Assert.IsType<AgentRuntime>(host.AgentRuntime);
            fixture.Request = fixture.Request with
            {
                WorkspaceSnapshot = ProjectWorkspacePaths.CreateSnapshot(projectId, Path.GetTempPath()),
            };
            Assert.True(host.HasActiveTurn);
            Assert.False(active.IsDisposed);
            fixture.Service.HoldTurn.SetResult();
            await host.WhenIdleAsync();
            fixture.Service.HoldTurn = null;
            Assert.Equal(original.Fingerprint, Assert.Single(fixture.Service.ExecutedWorkspaces)!.Fingerprint);
            Assert.Equal(original.Fingerprint, Assert.Single(fixture.Service.ChildWorkspaces)!.Fingerprint);

            var next = await fixture.RunTurnAsync();
            Assert.NotSame(active, next);
            Assert.True(active.IsDisposed);
            Assert.Equal(
                active.SessionStateScope!.ProjectConversationId,
                next.SessionStateScope!.ProjectConversationId
            );
            Assert.Equal(active.SessionStateScope.ContextId, next.SessionStateScope.ContextId);
            Assert.Equal(active.SessionStateScope.Generation, next.SessionStateScope.Generation);
            Assert.Empty(fixture.Service.ExecutedWorkspaces[1]!.AdditionalDirectories);
            Assert.Same(next, await fixture.RunTurnAsync());
        }
        finally
        {
            extra.Delete();
        }
    }

    [Fact]
    public async Task StartAsync_UnchangedDefinition_ReusesRuntime()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var first = await fixture.RunTurnAsync();

        // Act
        var second = await fixture.RunTurnAsync();

        // Assert
        Assert.Same(first, second);
        Assert.False(first.IsDisposed);
        Assert.Single(fixture.Service.CreatedAgents);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("credential")]
    [InlineData("workspace")]
    [InlineData("capabilities")]
    public async Task StartAsync_DependencyChangesWithoutAgentEdit_RebuildsRuntime(string change)
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var first = await fixture.RunTurnAsync();
        var originalVersion = fixture.Definition.UpdateTime;
        switch (change)
        {
            case "endpoint":
                fixture.Providers.Endpoint = "https://changed.example.com";
                break;
            case "credential":
                fixture.Providers.Key = "rotated-key";
                break;
            case "workspace":
                fixture.Projects.Workspace = "/changed";
                break;
            case "capabilities":
                fixture.Projects.Connections = [Guid.CreateVersion7()];
                break;
        }
        var second = await fixture.RunTurnAsync();
        Assert.NotSame(first, second);
        Assert.True(first.IsDisposed);
        Assert.Equal(originalVersion, fixture.Definition.UpdateTime);
        Assert.DoesNotContain("rotated-key", second.ConfigurationVersion ?? "");
    }

    [Theory]
    [InlineData(EngineKind.ClaudeCode)]
    [InlineData(EngineKind.Codex)]
    [InlineData(EngineKind.Pi)]
    public async Task StartAsync_UpdatedDefinition_RebuildsAndPreservesConversation(EngineKind kind)
    {
        // Arrange
        await using var fixture = new Fixture(kind);
        await fixture.InitializeAsync();
        var first = await fixture.RunTurnAsync();
        var oldAgent = Assert.IsType<RecordingAgent>(first.Agent);
        fixture.Definition.ModelProviderId = Guid.CreateVersion7();
        fixture.Definition.EnvironmentVariables = new() { ["VERSION"] = "new" };
        fixture.Definition.Extra = "{\"model\":\"new-model\"}";
        await fixture.UpdateAsync();

        // Act
        var second = await fixture.RunTurnAsync();

        // Assert
        Assert.NotSame(first, second);
        Assert.True(first.IsDisposed);
        Assert.True(oldAgent.Disposed);
        var newAgent = Assert.IsType<RecordingAgent>(second.Agent);
        Assert.Equal(fixture.Definition.ModelProviderId, newAgent.Definition.ModelProviderId);
        Assert.Equal("new", newAgent.Definition.EnvironmentVariables["VERSION"]);
        Assert.Equal(fixture.Definition.Extra, newAgent.Definition.Extra);
        Assert.NotEqual(oldAgent.Definition.ModelProviderId, newAgent.Definition.ModelProviderId);
        Assert.Equal(first.SessionStateScope!.ProjectConversationId, second.SessionStateScope!.ProjectConversationId);
        Assert.Equal(first.SessionStateScope.ContextId, second.SessionStateScope.ContextId);
        Assert.Equal(first.SessionStateScope.Generation, second.SessionStateScope.Generation);
        Assert.True(await fixture.Service.IsRuntimeCurrentAsync(second, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_ReplacementFails_ClearsDisposedRuntimeAndAllowsRetry()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var first = await fixture.RunTurnAsync();
        await fixture.UpdateAsync();
        fixture.Service.FailCreation = true;

        // Act
        await Assert.ThrowsAsync<AgwException>(() => fixture.RunTurnAsync());

        // Assert
        Assert.True(first.IsDisposed);
        Assert.Null(fixture.Coordinator.Host);
        fixture.Service.FailCreation = false;
        var recovered = await fixture.RunTurnAsync();
        Assert.NotSame(first, recovered);
        Assert.False(recovered.IsDisposed);
    }

    [Fact]
    public async Task StartAsync_DefinitionChangesDuringTurn_KeepsActiveRuntimeUntilNextTurn()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Service.HoldTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.StartAsync();
        var host = fixture.Host;
        var active = Assert.IsType<AgentRuntime>(host.AgentRuntime);

        try
        {
            // Act
            await fixture.UpdateAsync();

            // Assert
            Assert.True(host.HasActiveTurn);
            Assert.False(active.IsDisposed);
            Assert.Single(fixture.Service.CreatedAgents);
        }
        finally
        {
            fixture.Service.HoldTurn.SetResult();
            await host.WhenIdleAsync();
            fixture.Service.HoldTurn = null;
        }
        Assert.NotSame(active, await fixture.RunTurnAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsRuntimeCurrentAsync_MissingOrForeignDefinition_ReturnsFalse(bool foreign)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.RunTurnAsync();
        if (foreign)
            fixture.User.UserId = "another-user";
        else
        {
            fixture.Db.Agents.Remove(fixture.Definition);
            await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        var current = await fixture.Service.IsRuntimeCurrentAsync(runtime, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(current);
    }

    private sealed class TestProviders : IModelProviderReferenceFacade
    {
        public string Endpoint { get; set; } = "https://example.com";
        public string Key { get; set; } = "first-key";

        public Task<IReadOnlySet<Guid>> FilterVisibleModelProviderIdsAsync(
            IReadOnlyCollection<Guid> ids,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlySet<Guid>>(ids.ToHashSet());

        public Task<ModelProviderRuntimeSnapshot?> GetRuntimeSnapshotAsync(
            Guid id,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult<ModelProviderRuntimeSnapshot?>(
                new(
                    id,
                    new(Guid.Empty, "model", 1000, 100),
                    new(Guid.Empty, "provider", ProviderType.Anthropic, Endpoint, [new(true, Key)])
                )
            );
    }

    private sealed class TestProjects : IProjectRuntimeFacade
    {
        public string Workspace { get; set; } = "/original";
        public IReadOnlyList<Guid> Connections { get; set; } = [];

        public Task<ProjectRuntimeSnapshot?> GetForCurrentUserAsync(
            Guid id,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult<ProjectRuntimeSnapshot?>(
                new(id, "project", Workspace, null, [], new Dictionary<string, string>(), [], [], Connections)
            );

        public Task<string?> GetWorkspaceAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class Fixture : IAsyncDisposable, IAgwFileSystemResolver
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        public TestUserInfoService User { get; } = new();
        public TestProviders Providers { get; } = new();
        public TestProjects Projects { get; } = new();
        public AgwDbContext Db { get; }
        public Agent Definition { get; }
        public RecordingRuntimeFactory Service { get; }
        public ExecutionRequest Request { get; set; }

        public Fixture(EngineKind kind = EngineKind.ClaudeCode)
        {
            Db = new AgwDbContext(new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(_connection).Options);
            Definition = new Agent
            {
                Id = Guid.CreateVersion7(),
                Name = "refresh",
                DisplayName = "Refresh",
                CreateBy = "tester",
                CreateTime = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
                Type = AgentType.External,
                ExternalAgentKind = kind,
                ModelProviderId = Guid.CreateVersion7(),
            };
            var app = TestAgentAppService.Create(Db, null!, Providers, null!, User, null!);
            var configuration = new AgentRuntimeConfiguration(app, Projects);
            var checker = new AgentRuntimeFactory(
                app,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                NullLogger<AgentRuntimeFactory>.Instance,
                telemetryMiddleware: null!,
                summaryService: null!,
                services: _services,
                projectDefaults: new TestProjectDefaultResolver(),
                configuration: configuration,
                skillRegistrations: [],
                remoteSkillContentResolver: null,
                loggerFactory: NullLoggerFactory.Instance,
                conversationHistoryWriter: null,
                humanInteractionContextAccessor: null,
                executionContext: null,
                timeProvider: TimeProvider.System,
                generatedToolCatalog: null
            );
            Service = new RecordingRuntimeFactory(Db, checker, configuration);
            _turnExecutor = new AgentTurnExecutor(
                null,
                new AgentSessionStateStore(
                    _services.GetRequiredService<IServiceScopeFactory>(),
                    TimeProvider.System,
                    NullLogger<AgentSessionStateStore>.Instance
                ),
                null,
                NullLogger<AgentTurnExecutor>.Instance
            );
            var task = new AgentExecutionTask
            {
                TaskId = Guid.CreateVersion7(),
                ProjectId = Guid.CreateVersion7(),
                ProjectConversationId = Guid.CreateVersion7(),
                ContextId = "existing-chat",
                Generation = 3,
            };
            var target = new ExecutionTarget(Definition.Id, AgentRuntimeType.Agent);
            var turnId = Guid.CreateVersion7();
            Request = new ExecutionRequest(
                turnId,
                "tester",
                target,
                task,
                ExecutionSettings.CreateDefault(),
                new AgwUserInput { Contents = [] },
                Stream: false,
                ProjectWorkspacePaths.CreateSnapshot(task.ProjectId, Path.GetTempPath())
            )
            {
                Envelope = TurnPersistenceTestKit.CreateEnvelope(turnId, task, target),
            };
        }

        private readonly AgentTurnExecutor _turnExecutor;

        private TurnPersistenceTestKit? _persistence;

        public InProcessExecutionCoordinator Coordinator { get; private set; } = null!;

        public InProcessTurnHost Host => Assert.IsType<InProcessTurnHost>(Coordinator.Host);

        public async Task InitializeAsync()
        {
            await _connection.OpenAsync(TestContext.Current.CancellationToken);
            await Db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            Db.Agents.Add(Definition);
            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
            _persistence = await TurnPersistenceTestKit.CreateAsync();
            Coordinator = new InProcessExecutionCoordinator(
                Service,
                _turnExecutor,
                new AgentflowTurnExecutor(null!, null!, NullLogger<AgentflowTurnExecutor>.Instance),
                new ExecutionContextFactory(Db),
                this,
                conversationGate: null,
                _persistence.Broadcasts,
                _persistence.Services.GetRequiredService<IServiceScopeFactory>(),
                TestContext.Current.CancellationToken
            );
        }

        public async Task UpdateAsync()
        {
            Definition.UpdateTime = (Definition.UpdateTime ?? Definition.CreateTime).AddSeconds(1);
            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// 登记当前请求的 Turn 行与广播后交给协调器。
        /// Registers the current request's turn row and broadcast, then hands it to the coordinator.
        /// </summary>
        public async Task<ExecutionReceipt> StartAsync() =>
            await Coordinator.StartAsync(
                await _persistence!.RegisterTurnAsync(Request),
                TestContext.Current.CancellationToken
            );

        public async Task<AgentRuntime> RunTurnAsync()
        {
            var turnId = Guid.CreateVersion7();
            var receipt = await Coordinator.StartAsync(
                await _persistence!.RegisterTurnAsync(
                    Request with
                    {
                        TurnId = turnId,
                        Envelope = Request.Envelope with { TurnId = turnId },
                    }
                ),
                TestContext.Current.CancellationToken
            );
            Assert.True(receipt.Accepted);
            var host = Host;
            await host.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            return Assert.IsType<AgentRuntime>(host.AgentRuntime);
        }

        public Task<IAgwFileSystem?> ResolveAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IAgwFileSystem?>(new LocalFileSystem(Path.GetTempPath()));

        public async ValueTask DisposeAsync()
        {
            Service.HoldTurn?.TrySetResult();
            if (Coordinator != null)
                await Coordinator.ReleaseAsync();
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            await _services.DisposeAsync();
            if (_persistence != null)
                await _persistence.DisposeAsync();
        }
    }

    private sealed class RecordingRuntimeFactory : IAgentRuntimeFactory
    {
        private readonly AgwDbContext _db;
        private readonly AgentRuntimeFactory _checker;
        private readonly AgentRuntimeConfiguration _configuration;
        public List<RecordingAgent> CreatedAgents { get; } = [];
        public List<ProjectWorkspaceSnapshot?> ExecutedWorkspaces { get; } = [];
        public List<ProjectWorkspaceSnapshot?> ChildWorkspaces { get; } = [];
        public bool FailCreation { get; set; }
        public TaskCompletionSource? HoldTurn { get; set; }

        public RecordingRuntimeFactory(
            AgwDbContext db,
            AgentRuntimeFactory checker,
            AgentRuntimeConfiguration configuration
        )
        {
            _db = db;
            _checker = checker;
            _configuration = configuration;
        }

        public Task<bool> IsRuntimeCurrentAsync(AgentRuntime runtime, CancellationToken cancellationToken = default) =>
            _checker.IsRuntimeCurrentAsync(runtime, cancellationToken);

        public async Task<AgentRuntime?> CreateRuntimeAsync(
            Guid agentId,
            AgentExecutionTask task,
            ExecutionSettings settings,
            CancellationToken cancellationToken = default
        )
        {
            if (FailCreation)
                throw new AgwException(ErrorCodes.AiAgentCreationFailed);
            var definition = await _db
                .Agents.AsNoTracking()
                .SingleAsync(agent => agent.Id == agentId, cancellationToken);
            var agent = new RecordingAgent(definition, this, task.ProjectId);
            CreatedAgents.Add(agent);
            return new AgentRuntime(
                NullLogger.Instance,
                agent,
                await agent.CreateSessionAsync(cancellationToken),
                task.ProjectId,
                task.ContextId,
                new AgentSessionStateScope(
                    task.ProjectConversationId,
                    task.ProjectId,
                    task.ContextId,
                    agentId,
                    generation: task.Generation
                ),
                definition.Type
            )
            {
                ConfigurationVersion = await _configuration.ReadAsync(agentId, task.ProjectId, cancellationToken),
            };
        }

        public Task<AgentflowNodeAgent?> CreateAgentflowNodeAgentAsync(
            Guid agentId,
            Guid? projectId,
            Guid conversationId,
            IReadOnlyDictionary<string, string>? environmentVariables,
            bool deferHumanInteractions,
            CancellationToken cancellationToken = default,
            AgwPermissionMode? permissionMode = null
        ) => throw new NotSupportedException();

        public Task SetModeAsync(AgentRuntime runtime, string mode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingAgent : AIAgent, IAsyncDisposable
    {
        private readonly RecordingRuntimeFactory _owner;
        private readonly Guid _projectId;

        public Agent Definition { get; }
        public bool Disposed { get; private set; }

        public RecordingAgent(Agent definition, RecordingRuntimeFactory owner, Guid projectId)
        {
            Definition = definition;
            _owner = owner;
            _projectId = projectId;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new RecordingSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        // 在真实执行位置观察工作区上下文：Agent 运行期间当前上下文与派生任务看到的快照都记录下来。
        // Observe the workspace context where the turn actually runs: both the ambient snapshot and the one a derived task sees are recorded.
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            if (_owner.HoldTurn != null)
            {
                await _owner.HoldTurn.Task.WaitAsync(cancellationToken);
            }

            _owner.ExecutedWorkspaces.Add(ExecutionContextSlot.GetWorkspaceSnapshot(_projectId));
            _owner.ChildWorkspaces.Add(
                await Task.Run(() => ExecutionContextSlot.GetWorkspaceSnapshot(_projectId), cancellationToken)
            );
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSession : AgentSession { }
}
