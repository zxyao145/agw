using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Execution.Agents.ExternalAgents.A2A;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Messaging.Durable;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Agents.Execution.Turns;
using Agw.Agents.Tests.A2A;
using Agw.Auth.Extensions;
using Agw.Files;
using Agw.Host.Data;
using Agw.Infrastructure;
using Agw.Infrastructure.Data;
using Agw.Integrations.Extensions;
using Agw.Jobs;
using Agw.Notifications;
using Agw.Projects;
using Agw.Projects.Application;
using Agw.Providers;
using Agw.Settings;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Runtime;
using Agw.Skills;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Agw.Agents.Tests;

internal sealed class ExternalAgentHarness : IAsyncDisposable
{
    private const string UserId = "1001";
    internal const string ContextId = "external-agent-conversation";
    private readonly string _root;
    private readonly ServiceProvider _services;
    private readonly bool _distributed;
    private readonly CancellationTokenSource _host = new();
    private AsyncServiceScope _connection;
    private InProcessExecutionCoordinator? _coordinator;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ExternalAgentHarness(string root, ServiceProvider services, bool distributed)
    {
        _root = root;
        _services = services;
        _distributed = distributed;
        _connection = services.CreateAsyncScope();
    }

    public Guid AgentId { get; } = Guid.CreateVersion7();
    public Guid ProjectId { get; } = Guid.CreateVersion7();
    public Guid ConversationId { get; } = Guid.CreateVersion7();
    public string WorkspacePath => Path.Combine(_root, "workspace");
    public IServiceProvider Services => _connection.ServiceProvider;
    public InProcessTurnHost? TurnHost => _coordinator?.Host;

    public static IDisposable EnterUser() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "Test"))
        );

    public static Task<ExternalAgentHarness> CreateAsync(
        A2ATestAgentServer server,
        IReadOnlyList<A2AOriginCredentialsRequest>? credentials = null,
        bool createWorkspace = true,
        string? postgres = null
    ) =>
        CreateAsync(
            new Agent
            {
                Name = "a2a",
                DisplayName = "A2A",
                Type = AgentType.External,
                ExternalAgentKind = EngineKind.A2A,
            },
            new() { A2AEndpoint = new A2AEndpointRequest(server.CardUrl.AbsoluteUri, credentials).ToInput() },
            createWorkspace,
            postgres
        );

    internal static async Task<ExternalAgentHarness> CreateAsync(
        Agent agent,
        AgentCreateCommand command,
        bool createWorkspace = true,
        string? postgres = null,
        Action<Dictionary<string, string?>>? configure = null
    )
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", $"{agent.Name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var provider = BuildServices(root, CreateConfiguration(root, postgres, configure));
        var harness = new ExternalAgentHarness(root, provider, postgres is not null);
        try
        {
            await harness.SeedAsync(agent, command, createWorkspace);
            if (postgres is null)
            {
                harness._coordinator = harness
                    .Services.GetRequiredService<InProcessExecutionCoordinatorFactory>()
                    .Create(harness._host.Token);
            }
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    private static IConfiguration CreateConfiguration(
        string root,
        string? postgres,
        Action<Dictionary<string, string?>>? configure
    )
    {
        var connection = postgres is null
            ? $"Data Source={Path.Combine(root, "agw.db")};Pooling=False"
            : new NpgsqlConnectionStringBuilder(postgres)
            {
                Database = $"agw_external_{Guid.NewGuid():N}",
                Pooling = false,
            }.ConnectionString;
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = postgres is null ? "sqlite" : "postgres",
            ["Database:ConnectionString"] = connection,
            ["Execution:Provider"] = postgres is null ? "InProcess" : "Distributed",
            ["Extensions:Directory"] = Path.Combine(root, "extensions"),
        };
        configure?.Invoke(settings);
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static ServiceProvider BuildServices(string root, IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(AgwDataPaths.Resolve(Path.Combine(root, "data"), root, Path.Combine(root, "logs")));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddScoped<IEntityAuditUserIdProvider, EntityAuditUserIdProvider>();
        services
            .AddTools(configuration)
            .AddNotifications(configuration, runWorkers: false)
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
            .AddTestExtensions(configuration)
            .AddJobs()
            .AddProviders(configuration)
            .AddSkills(configuration)
            .AddProjects(configuration)
            .AddAuth()
            .AddSettings()
            .AddIntegrations(configuration);
        return services.BuildServiceProvider();
    }

    private async Task SeedAsync(Agent agent, AgentCreateCommand command, bool createWorkspace)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        if (_distributed)
            await db.Database.MigrateAsync(Ct);
        else
            await db.Database.EnsureCreatedAsync(Ct);
        if (createWorkspace)
            Directory.CreateDirectory(WorkspacePath);
        db.Projects.Add(
            new Project
            {
                Id = ProjectId,
                Name = agent.DisplayName,
                Type = ProjectType.UserDefined,
                Workspace = WorkspacePath,
            }
        );
        db.ProjectConversations.Add(
            new ProjectConversation
            {
                Id = ConversationId,
                ProjectId = ProjectId,
                ContextId = ContextId,
                Title = agent.DisplayName,
            }
        );
        await db.SaveChangesAsync(Ct);
        var app = scope.ServiceProvider.GetRequiredService<AgentAppService>();
        agent.Id = AgentId;
        Assert.NotNull(await app.CreateAgentAsync(agent, command, Ct));
    }

    public async Task<Guid> StartTurnAsync(
        string text,
        AgwPermissionMode? permission = null,
        Guid? agentId = null,
        AgentRuntimeType runtimeType = AgentRuntimeType.Agent
    )
    {
        var acceptance = Services.GetRequiredService<TurnAcceptanceService>();
        var accepted = await acceptance.AcceptAsync(
            new TurnAcceptanceRequest(
                UserId,
                TurnId: null,
                new ExecutionTarget(agentId ?? AgentId, runtimeType),
                ConversationId,
                TurnPersistenceTestKit.CreateInput(text),
                SettingCommandMapper.FromCommand(
                    new SettingCommand(ProjectId, ConversationId, permissionMode: permission)
                ),
                Stream: true
            ),
            Ct
        );

        if (_distributed)
        {
            var leases = Services.GetRequiredService<IDurableExecutionLeases>();
            var lease = await leases.TryClaimAsync(accepted.Request.TurnId, "a2a-test", TimeSpan.FromSeconds(30), Ct);
            Assert.NotNull(lease);
            await Services.GetRequiredService<DurableExecutionCoordinator>().RunLeasedSegmentAsync(lease, Ct);
        }
        else
        {
            try
            {
                await accepted.Broadcast!.WriteAsync(accepted.Start, Ct);
                Assert.True((await _coordinator!.StartAsync(accepted.Request, Ct)).Accepted);
            }
            catch (Exception failure)
            {
                await acceptance.ReportStartFailureAsync(accepted, failure);
                throw;
            }
        }
        return accepted.Request.TurnId;
    }

    public async Task<Guid> RunTurnAsync(string text, Guid? agentId = null)
    {
        var id = await StartTurnAsync(text, agentId: agentId);
        await WaitIdleAsync();
        return id;
    }

    public Task WaitIdleAsync() =>
        _coordinator?.Host?.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15), Ct) ?? Task.CompletedTask;

    public void Interrupt() => _coordinator!.Host!.RequestInterrupt();

    public async Task RebuildCoordinatorAsync()
    {
        await _coordinator!.ReleaseAsync();
        await _connection.DisposeAsync();
        _connection = _services.CreateAsyncScope();
        _coordinator = Services.GetRequiredService<InProcessExecutionCoordinatorFactory>().Create(_host.Token);
    }

    public async Task ClearConversationAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var result = await scope
            .ServiceProvider.GetRequiredService<ProjectConversationAppService>()
            .ClearRecordsAsync(ProjectId, ConversationId);
        Assert.Equal(ApplicationResultType.Success, result.Type);
    }

    public async Task UpdateEndpointAsync(A2AEndpointRequest endpoint)
    {
        await using var scope = _services.CreateAsyncScope();
        var app = scope.ServiceProvider.GetRequiredService<AgentAppService>();
        Assert.NotNull(
            await app.UpdateAgentAsync(AgentId, new AgentUpdateRequest { A2AEndpoint = endpoint }.ToCommand(), Ct)
        );
    }

    public async Task<AgentA2AEndpoint> RefreshCardAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var app = scope.ServiceProvider.GetRequiredService<AgentAppService>();
        var agent = await app.RefreshA2ACardAsync(AgentId, Ct);
        return Assert.IsType<AgentA2AEndpoint>(agent?.A2AEndpoint);
    }

    public IReadOnlyList<AgwMessage> ReadMessages(Guid turnId) =>
        _services
            .GetRequiredService<TurnBroadcastRegistry>()
            .Find(turnId, UserId)
            ?.ReadAfter(0, out _)
            .Select(entry => entry.Message)
            .ToList()
        ?? [];

    public async Task<IReadOnlyList<AgwMessage>> ReadDurableMessagesAsync(Guid turnId) =>
        (await _services.GetRequiredService<DurableExecutionEventLog>().ReadAsync(turnId, 0, Ct))
            .Select(entry => entry.Message)
            .ToArray();

    public Task<ProjectConversationTurn> ReadTurnAsync(Guid turnId) =>
        QueryAsync(db => db.ProjectConversationTurns.AsNoTracking().SingleAsync(turn => turn.Id == turnId, Ct));

    public Task<List<ProjectConversationChatHistory>> ReadHistoryAsync() =>
        QueryAsync(db =>
            db.ProjectConversationChatHistories.AsNoTracking()
                .Where(row => row.ConversationId == ConversationId)
                .ToListAsync(Ct)
        );

    public async Task<A2ARemoteAgentSession> ReadSessionAsync()
    {
        var entry = await QueryAsync(db =>
            db.AgentSessionStates.AsNoTracking()
                .SingleAsync(state => state.ProjectConversationId == ConversationId && state.AgentId == AgentId, Ct)
        );
        using var json = JsonDocument.Parse(entry.SerializedSession);
        return A2ARemoteAgentSession.Deserialize(json.RootElement, null);
    }

    public async Task<T> QueryAsync<T>(Func<AgwDbContext, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AgwDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        if (_coordinator is not null)
            await _coordinator.ReleaseAsync();
        await _host.CancelAsync();
        await _connection.DisposeAsync();
        if (_distributed)
            await QueryAsync(db => db.Database.EnsureDeletedAsync(CancellationToken.None));
        await _services.DisposeAsync();
        _host.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
