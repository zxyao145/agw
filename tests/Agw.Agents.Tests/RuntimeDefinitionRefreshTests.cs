using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.Contracts;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Agents.Execution.Turns;
using Agw.Files.Abstracts;
using Agw.Files.Application.Storage.Local;
using Agw.Infrastructure.Data;
using Agw.Projects.Contracts.Runtime;
using Agw.Providers.Contracts;
using Agw.Providers.Contracts.References;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class RuntimeDefinitionRefreshTests
{
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
    [InlineData(ExternalAgentKind.ClaudeCode)]
    [InlineData(ExternalAgentKind.Codex)]
    [InlineData(ExternalAgentKind.Pi)]
    public async Task StartAsync_UpdatedDefinition_RebuildsAndPreservesConversation(ExternalAgentKind kind)
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
        Assert.Null(fixture.Starter.Runtime);
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
        await fixture.Starter.StartAsync(fixture.Request, TestContext.Current.CancellationToken);
        var active = Assert.IsType<AgentRuntime>(fixture.Starter.Runtime);

        try
        {
            // Act
            await fixture.UpdateAsync();

            // Assert
            Assert.True(active.HasActiveTurn);
            Assert.False(active.IsDisposed);
            Assert.Single(fixture.Service.CreatedAgents);
        }
        finally
        {
            fixture.Service.HoldTurn.SetResult();
            await active.WhenIdleAsync();
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

    private sealed class Fixture : IAsyncDisposable, IAgwFileSystemResolver, IExecutionMessageSink
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        public TestUserInfoService User { get; } = new();
        public TestProviders Providers { get; } = new();
        public TestProjects Projects { get; } = new();
        public AgwDbContext Db { get; }
        public Agent Definition { get; }
        public RecordingRuntimeService Service { get; }
        public InProcessExecutionStarter Starter { get; }
        public ExecutionStartRequest Request { get; }

        public Fixture(ExternalAgentKind kind = ExternalAgentKind.ClaudeCode)
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
            var app = new AgentAppService(Db, null!, Providers, null!, User, null!);
            var configuration = new AgentRuntimeConfiguration(app, Projects);
            var checker = new AgentRuntimeService(
                app,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                NullLogger<AgentRuntimeService>.Instance,
                null!,
                null!,
                null!,
                services: _services,
                projectDefaults: new TestProjectDefaultResolver(),
                turnExecutor: null!,
                configuration: configuration
            );
            Service = new RecordingRuntimeService(Db, checker, configuration);
            var factory = new RuntimeFactory(
                Service,
                null!,
                this,
                new RuntimeTurnContextAccessor(),
                new HumanInteractionContextAccessor()
            );
            Starter = new InProcessExecutionStarter(
                factory,
                "tester",
                this,
                TestContext.Current.CancellationToken,
                _ => { }
            );
            var task = new AgentExecutionTask
            {
                TaskId = Guid.CreateVersion7(),
                ProjectId = Guid.CreateVersion7(),
                ProjectConversationId = Guid.CreateVersion7(),
                ContextId = "existing-chat",
                Generation = 3,
            };
            Request = new ExecutionStartRequest(
                Guid.CreateVersion7(),
                new ExecutionTarget(Definition.Id, AgentRuntimeType.Agent),
                task,
                ExecutionSettings.CreateDefault(),
                new AgwUserInput { Contents = [] },
                false,
                Path.GetTempPath()
            );
        }

        public async Task InitializeAsync()
        {
            await _connection.OpenAsync(TestContext.Current.CancellationToken);
            await Db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            Db.Agents.Add(Definition);
            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task UpdateAsync()
        {
            Definition.UpdateTime = (Definition.UpdateTime ?? Definition.CreateTime).AddSeconds(1);
            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task<AgentRuntime> RunTurnAsync()
        {
            var receipt = await Starter.StartAsync(
                Request with
                {
                    ExecutionId = Guid.CreateVersion7(),
                },
                TestContext.Current.CancellationToken
            );
            Assert.True(receipt.Accepted);
            var runtime = Assert.IsType<AgentRuntime>(Starter.Runtime);
            await runtime.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            return runtime;
        }

        public Task<IAgwFileSystem?> ResolveAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IAgwFileSystem?>(new LocalFileSystem(Path.GetTempPath()));

        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            Service.HoldTurn?.TrySetResult();
            await Starter.ReleaseRuntimeAsync();
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class RecordingRuntimeService : IAgentRuntimeService
    {
        private readonly AgwDbContext _db;
        private readonly AgentRuntimeService _checker;
        private readonly AgentRuntimeConfiguration _configuration;
        public List<RecordingAgent> CreatedAgents { get; } = [];
        public bool FailCreation { get; set; }
        public TaskCompletionSource? HoldTurn { get; set; }

        public RecordingRuntimeService(
            AgwDbContext db,
            AgentRuntimeService checker,
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
            var agent = new RecordingAgent(definition);
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

        public Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
            AgentRuntime session,
            AgwUserInput input,
            IInteractionHandler? approvalHandler,
            CancellationToken cancellationToken = default
        ) => ExecuteAsync(session, input, cancellationToken);

        public async Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
            AgentRuntime session,
            AgwUserInput input,
            CancellationToken cancellationToken = default
        )
        {
            if (HoldTurn != null)
                await HoldTurn.Task.WaitAsync(cancellationToken);
            return [];
        }

        public Task<AIAgent?> CreateAiAgentAsync(Guid agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AIAgent?> CreateAiAgentAsync(
            Guid agentId,
            Guid? projectId,
            bool resume,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<AIAgent?> CreateAiAgentAsync(
            Guid agentId,
            Guid? projectId,
            bool resume,
            IReadOnlyDictionary<string, string>? environmentVariables,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
            AgentRuntime session,
            AgwUserInput input,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<Agw.Agents.Execution.Agents.Contracts.AgentExecutionResult?> ExecuteByIdAsync(
            AgentExecuteByIdRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class RecordingAgent : AIAgent, IAsyncDisposable
    {
        public Agent Definition { get; }
        public bool Disposed { get; private set; }

        public RecordingAgent(Agent definition)
        {
            Definition = definition;
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

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await Task.CompletedTask;
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
