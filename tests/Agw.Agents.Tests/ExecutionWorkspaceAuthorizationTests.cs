using System.Security.Claims;
using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.Agentflows.Turns;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Extensions;
using Agw.Files;
using Agw.Files.Abstracts;
using Agw.Host.Data;
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
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Tooling;
using Agw.Shared.Utils;
using Agw.Skills;
using Agw.Tools.Impl.ContextualTools.Shell;
using Agw.Tools.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public sealed class ExecutionWorkspaceAuthorizationTests
{
    private const string UserId = "workspace-user";
    private const string ForeignUserId = "foreign-workspace-user";

    [Fact]
    public async Task StartAsync_ForeignWorkspace_RejectsBeforeStartingAgentflow()
    {
        await using var fixture = await WorkspaceAuthorizationFixture.CreateAsync();
        var seed = await fixture.SeedAsync();
        Directory.Delete(seed.ForeignWorkspace, recursive: true);
        await fixture.SetWorkspaceAsync(seed.Project.Id, seed.ForeignWorkspace);

        using var user = EnterUser(UserId);
        var request = await fixture.AcceptAgentflowTurnAsync(seed);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Coordinator.StartAsync(request, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        Assert.Null(fixture.Coordinator.Host);
        Assert.False(Directory.Exists(seed.ForeignWorkspace));
    }

    [Fact]
    public async Task StartAsync_ReusedAgentflow_ValidatesNewWorkspaceBeforeReuse()
    {
        await using var fixture = await WorkspaceAuthorizationFixture.CreateAsync();
        var seed = await fixture.SeedAsync();
        Directory.Delete(seed.ForeignWorkspace, recursive: true);

        using var user = EnterUser(UserId);
        var firstRequest = await fixture.AcceptAgentflowTurnAsync(seed);
        var receipt = await fixture.Coordinator.StartAsync(firstRequest, TestContext.Current.CancellationToken);
        Assert.True(receipt.Accepted);
        var firstRuntime = Assert.IsType<AgentflowRuntime>(fixture.Coordinator.Host!.AgentflowRuntime);
        await fixture.Coordinator.Host.WhenIdleAsync();

        await fixture.SetWorkspaceAsync(seed.Project.Id, seed.ForeignWorkspace);
        var secondRequest = await fixture.AcceptAgentflowTurnAsync(seed);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Coordinator.StartAsync(secondRequest, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        Assert.Same(firstRuntime, fixture.Coordinator.Host.AgentflowRuntime);
        Assert.False(Directory.Exists(seed.ForeignWorkspace));
    }

    [Fact]
    public async Task MaterializeAsync_ForeignAdditionalDirectory_RejectsBeforeCreatingPrimaryDirectory()
    {
        await using var fixture = await WorkspaceAuthorizationFixture.CreateAsync();
        var seed = await fixture.SeedAsync();
        Directory.Delete(seed.ForeignWorkspace, recursive: true);
        var primary = Path.Combine(fixture.Root, "shell-primary");
        var context = CreateShellContext(
            seed.Project,
            primary,
            [new ProjectWorkspaceDirectory(Guid.CreateVersion7(), seed.ForeignWorkspace)]
        );
        var tool = new ShellContextualTool(fixture.Configuration, fixture.WorkspaceAccess);

        using var user = EnterUser(UserId);
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            tool.MaterializeAsync(new RunShellToolDefinition(), context, TestContext.Current.CancellationToken).AsTask()
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        Assert.False(Directory.Exists(primary));
        Assert.False(Directory.Exists(seed.ForeignWorkspace));
    }

    [Fact(SkipUnless = nameof(SymbolicLinksSupported), Skip = "Symbolic links and the local shell are required.")]
    public async Task RunShell_ManagedAliasChangesOwner_RejectsBeforeRecreatingExecutor()
    {
        await using var fixture = await WorkspaceAuthorizationFixture.CreateAsync();
        var seed = await fixture.SeedAsync();
        var workspace = Path.Combine(fixture.Root, "shell-alias");
        var context = CreateShellContext(seed.Project, workspace, []);
        var tool = new ShellContextualTool(fixture.Configuration, fixture.WorkspaceAccess);

        using var user = EnterUser(UserId);
        await using var contribution = await tool.MaterializeAsync(
            new RunShellToolDefinition(),
            context,
            TestContext.Current.CancellationToken
        );
        var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(contribution.Tools));
        Directory.Move(workspace, workspace + "-original");
        Directory.CreateSymbolicLink(workspace, seed.ForeignWorkspace);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            function
                .InvokeAsync(new AIFunctionArguments { ["command"] = "pwd" }, TestContext.Current.CancellationToken)
                .AsTask()
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        Assert.True(Directory.Exists(seed.ForeignWorkspace));
    }

    public static bool SymbolicLinksSupported => !OperatingSystem.IsWindows();

    private static IDisposable EnterUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Test"))
        );

    private static ToolMaterializationContext CreateShellContext(
        Project project,
        string workspace,
        IReadOnlyList<ProjectWorkspaceDirectory> additionalDirectories
    ) =>
        new()
        {
            Agent = new Agent { Id = Guid.CreateVersion7() },
            Project = project,
            Workspace = workspace,
            WorkspaceSnapshot = ProjectWorkspacePaths.CreateSnapshot(project.Id, workspace, additionalDirectories),
            DefaultMode = "execute",
        };

    private sealed class WorkspaceAuthorizationFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly AsyncServiceScope _connectionScope;
        private readonly CancellationTokenSource _host = new();

        private WorkspaceAuthorizationFixture(string root, IConfiguration configuration, ServiceProvider services)
        {
            Root = root;
            Configuration = configuration;
            _services = services;
            _connectionScope = services.CreateAsyncScope();
            var scoped = _connectionScope.ServiceProvider;
            var gate = scoped.GetRequiredService<IConversationExecutionGate>();
            Coordinator = new InProcessExecutionCoordinatorFactory(
                scoped.GetRequiredService<IAgentRuntimeFactory>(),
                scoped.GetRequiredService<AgentTurnExecutor>(),
                scoped.GetRequiredService<AgentflowTurnExecutor>(),
                scoped.GetRequiredService<ExecutionContextFactory>(),
                scoped.GetRequiredService<IAgwFileSystemResolver>(),
                scoped.GetRequiredService<IProjectWorkspaceAccess>(),
                scoped.GetRequiredService<TurnBroadcastRegistry>(),
                scoped.GetRequiredService<IServiceScopeFactory>(),
                gate
            ).Create(_host.Token);
        }

        public string Root { get; }

        public IConfiguration Configuration { get; }

        public IProjectWorkspaceAccess WorkspaceAccess => _services.GetRequiredService<IProjectWorkspaceAccess>();

        public InProcessExecutionCoordinator Coordinator { get; }

        public static async Task<WorkspaceAuthorizationFixture> CreateAsync()
        {
            var token = TestContext.Current.CancellationToken;
            var root = Path.Combine(
                AppContext.BaseDirectory,
                ".scratch",
                "execution-workspace-authorization",
                Guid.CreateVersion7().ToString("N")
            );
            Directory.CreateDirectory(root);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Database:Provider"] = "sqlite",
                        ["Database:ConnectionString"] = $"Data Source={Path.Combine(root, "agw.db")};Pooling=False",
                        ["Execution:Provider"] = "InProcess",
                        ["Projects:Workspace:IsolationEnabled"] = "true",
                        ["Agents:Shell:Backend"] = "local",
                    }
                )
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(AgwDataPaths.Resolve(Path.Combine(root, "data"), root, Path.Combine(root, "logs")));
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton(TimeProvider.System);
            services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")));
            services.AddHttpClient();
            services.AddScoped<IEntityAuditUserIdProvider, EntityAuditUserIdProvider>();
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

            var fixture = new WorkspaceAuthorizationFixture(root, configuration, services.BuildServiceProvider());
            await using var scope = fixture._services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AgwDbContext>().Database.EnsureCreatedAsync(token);
            return fixture;
        }

        public async Task<WorkspaceSeed> SeedAsync()
        {
            var project = await CreateProjectAsync(UserId, "workspace-current");
            var foreign = await CreateProjectAsync(ForeignUserId, "workspace-foreign");
            var agentflowId = Guid.CreateVersion7();
            var conversationId = Guid.CreateVersion7();
            const string contextId = "workspace-authorization";
            using (EnterUser(UserId))
            {
                await using var scope = _services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
                var now = TimeProvider.System.GetUtcNow();
                context.ProjectConversations.Add(
                    new ProjectConversation
                    {
                        Id = conversationId,
                        ProjectId = project.Id,
                        ContextId = contextId,
                        Title = "workspace authorization",
                        CreateBy = UserId,
                        CreateTime = now,
                    }
                );
                context.Agentflows.Add(
                    new Agentflow
                    {
                        Id = agentflowId,
                        Name = "workspace authorization",
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
                                EdgeId = "input-output",
                                SourceNodeId = "input",
                                TargetNodeId = "output",
                                CreateBy = UserId,
                                CreateTime = now,
                            },
                        ],
                    }
                );
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            Assert.NotNull(foreign.Workspace);
            return new WorkspaceSeed(project, foreign.Workspace, agentflowId, conversationId);
        }

        public async Task SetWorkspaceAsync(Guid projectId, string workspace)
        {
            using var user = EnterUser(UserId);
            await using var scope = _services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            var project = await context.Projects.SingleAsync(
                item => item.Id == projectId,
                TestContext.Current.CancellationToken
            );
            project.Workspace = workspace;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task<ExecutionRequest> AcceptAgentflowTurnAsync(WorkspaceSeed seed)
        {
            using var user = EnterUser(UserId);
            await using var scope = _services.CreateAsyncScope();
            var accepted = await scope
                .ServiceProvider.GetRequiredService<TurnAcceptanceService>()
                .AcceptAsync(
                    new TurnAcceptanceRequest(
                        UserId,
                        TurnId: null,
                        new ExecutionTarget(seed.AgentflowId, AgentRuntimeType.Agentflow),
                        seed.ConversationId,
                        TurnPersistenceTestKit.CreateInput("continue"),
                        SettingCommandMapper.FromCommand(new SettingCommand(seed.Project.Id, seed.ConversationId)),
                        Stream: false
                    ),
                    TestContext.Current.CancellationToken
                );
            return accepted.Request;
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.ReleaseAsync();
            await _connectionScope.DisposeAsync();
            _host.Dispose();
            await _services.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private async Task<Project> CreateProjectAsync(string userId, string name)
        {
            using var user = EnterUser(userId);
            await using var scope = _services.CreateAsyncScope();
            var project = await scope
                .ServiceProvider.GetRequiredService<IProjectAppService>()
                .CreateAsync(new Project { Name = name, Type = ProjectType.UserDefined }, null, null, null);
            return Assert.IsType<Project>(project);
        }
    }

    private sealed record WorkspaceSeed(
        Project Project,
        string ForeignWorkspace,
        Guid AgentflowId,
        Guid ConversationId
    );
}
