using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.Agentflows.Turns;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Extensions;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Packages;
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
using Agw.Providers.Contracts;
using Agw.Settings;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Tooling;
using Agw.Shared.Utils;
using Agw.Skills;
using Agw.Tools.Impl.ContextualTools.Shell;
using Agw.Tools.Runtime;
using Microsoft.Agents.AI;
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
    public async Task GetOrCreateHostForTurnAsync_CompiledExtension_ReusesHostAndRebuildsForNewEpoch()
    {
        var token = TestContext.Current.CancellationToken;
        var extensionId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48a100");
        var extensionRoot = Path.Combine(
            AppContext.BaseDirectory,
            ".scratch",
            "in-process-host-extension",
            Guid.CreateVersion7().ToString("N")
        );
        var extensionDirectory = Path.Combine(extensionRoot, "extensions");
        Directory.CreateDirectory(extensionDirectory);

        try
        {
            await TurnAcceptanceTests.CreateCompiledExtensionPackageAsync(extensionDirectory, extensionId, token);
            await using var fixture = await WorkspaceAuthorizationFixture.CreateAsync(extensionDirectory);
            var seed = await fixture.SeedAsync();
            var agentId = await fixture.SeedSystemAgentAsync(extensionId);

            using var user = EnterUser(UserId);
            var runtimeDataPaths = fixture.Services.GetRequiredService<AgwDataPaths>();
            var packages = fixture.Services.GetRequiredService<ExtensionPackageCatalog>();
            await packages.InitializeAsync(token);
            var package = packages.GetValidPackage(extensionId);
            var server = package.Manifest.Server!;
            var loadRequest = new ExtensionRuntimeLoadRequest
            {
                ExtensionId = extensionId,
                Name = package.Manifest.Name,
                Version = package.Manifest.Version,
                ReleaseHash = package.ReleaseHash,
                Epoch = 0,
                DataDirectory = Path.Combine(runtimeDataPaths.Root, "extensions", "data", extensionId.ToString("N")),
                PackageDirectory = package.SnapshotDirectory,
                EntryAssemblyPath = server.EntryAssembly,
                EntryType = server.EntryType,
            };
            var enablement = fixture.Services.GetRequiredService<IExtensionEnablementStore>();
            var extensionRuntime = fixture.Services.GetRequiredService<IExtensionRuntime>();
            var enabled = await enablement.SetAsync(extensionId, true, token);
            loadRequest = loadRequest with { Epoch = enabled.Version };
            ExtensionGenerationSnapshot generation = await extensionRuntime.LoadAsync(loadRequest, token);
            await extensionRuntime.PrepareDatabaseAsync(generation.GenerationId, token);
            await extensionRuntime.ActivateAsync(generation.GenerationId, token);
            await extensionRuntime.ResumeAsync(generation.GenerationId, token);

            var firstRequest = await fixture.AcceptAgentTurnAsync(seed, agentId);
            var firstCapabilities = Assert.IsType<ExtensionCapabilitySnapshot>(
                firstRequest.Settings.ExtensionCapabilityLease
            );
            var firstHost = await fixture.Coordinator.GetOrCreateHostForTurnAsync(firstRequest, false, token);
            Assert.NotNull(firstHost);
            await AssertCompiledToolCallAsync(firstHost!, "first", token);
            await fixture.Coordinator.ReleaseExecutionLeaseAsync(
                Task.CompletedTask,
                null,
                new CancellationTokenSource(),
                firstRequest.Settings,
                firstRequest.TurnId,
                firstCapabilities
            );

            var secondRequest = await fixture.AcceptAgentTurnAsync(seed, agentId);
            var secondHost = await fixture.Coordinator.GetOrCreateHostForTurnAsync(secondRequest, false, token);
            Assert.Same(firstHost, secondHost);
            await AssertCompiledToolCallAsync(secondHost!, "second", token);
            await fixture.Coordinator.ReleaseExecutionLeaseAsync(
                Task.CompletedTask,
                null,
                new CancellationTokenSource(),
                secondRequest.Settings,
                secondRequest.TurnId,
                firstCapabilities
            );

            var agentflowId = await fixture.SeedAgentflowWithAgentAsync(agentId);
            var agentflowRequest = await fixture.AcceptAgentflowTurnAsync(seed, agentflowId);
            var agentflowHost = await fixture.Coordinator.GetOrCreateHostForTurnAsync(agentflowRequest, false, token);
            Assert.NotNull(agentflowHost?.AgentflowRuntime);
            var agentflowRequestForTurn = fixture.Coordinator.ApplyAgentflowSettingsForTurn(
                agentflowHost!,
                agentflowRequest
            );
            var agentflowCapabilities = Assert.IsType<ExtensionCapabilitySnapshot>(
                agentflowRequestForTurn.Settings.ExtensionCapabilityLease
            );
            Assert.Equal(firstCapabilities.Pins, agentflowCapabilities.Pins);
            await AssertCompiledAgentflowToolCallAsync(
                fixture.Services,
                agentflowRequestForTurn,
                agentflowCapabilities,
                token
            );
            await fixture.Coordinator.ReleaseExecutionLeaseAsync(
                Task.CompletedTask,
                null,
                new CancellationTokenSource(),
                agentflowRequest.Settings,
                agentflowRequest.TurnId,
                agentflowCapabilities
            );

            await enablement.SetAsync(extensionId, false, token);
            Assert.True(await extensionRuntime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), token));
            await extensionRuntime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), token);
            var rejected = await Assert.ThrowsAsync<AgwException>(async () =>
            {
                await using var unavailable = await fixture
                    .Services.GetRequiredService<IExtensionCapabilityCatalog>()
                    .AcquireCurrentAsync(
                        new ExtensionCapabilitySelection(
                            [new ExtensionToolSelection(extensionId, "calendar-lookup")],
                            []
                        ),
                        token
                    );
            });
            Assert.Equal(ErrorCodes.ExtensionUnavailable.Code, rejected.Code);

            var reenabled = await enablement.SetAsync(extensionId, true, token);
            var nextGeneration = await extensionRuntime.LoadAsync(
                loadRequest with
                {
                    Epoch = reenabled.Version,
                },
                token
            );
            await extensionRuntime.PrepareDatabaseAsync(nextGeneration.GenerationId, token);
            await extensionRuntime.ActivateAsync(nextGeneration.GenerationId, token);
            await extensionRuntime.ResumeAsync(nextGeneration.GenerationId, token);

            await ExerciseReenabledHostAsync(fixture, seed, agentId, firstHost!, token);
            Assert.True(await extensionRuntime.DrainAsync(nextGeneration.GenerationId, TimeSpan.FromSeconds(1), token));
            await extensionRuntime.UnloadAsync(nextGeneration.GenerationId, TimeSpan.FromSeconds(5), token);
        }
        finally
        {
            if (Directory.Exists(extensionRoot))
                Directory.Delete(extensionRoot, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ExerciseReenabledHostAsync(
        WorkspaceAuthorizationFixture fixture,
        WorkspaceSeed seed,
        Guid agentId,
        InProcessTurnHost previousHost,
        CancellationToken cancellationToken
    )
    {
        var request = await fixture.AcceptAgentTurnAsync(seed, agentId);
        var host = await fixture.Coordinator.GetOrCreateHostForTurnAsync(request, false, cancellationToken);
        Assert.NotNull(host);
        Assert.NotSame(previousHost, host);
        await AssertCompiledToolCallAsync(host!, "new-epoch", cancellationToken);
        await fixture.Coordinator.ReleaseAsync();
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

    private static async Task AssertCompiledToolCallAsync(
        InProcessTurnHost host,
        string value,
        CancellationToken cancellationToken
    )
    {
        var runtime = Assert.IsType<AgentRuntime>(host.AgentRuntime);
        var options = AgentRuntimeFactorySystemCompositionTests.FindInObjectGraph<ChatClientAgentOptions>(
            runtime.Agent
        );
        var chatOptions = Assert.IsType<ChatOptions>(options.ChatOptions);
        var function = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(chatOptions.Tools!, tool => tool.Name == "calendar_lookup")
        );
        var result = await function.InvokeAsync(new AIFunctionArguments { ["value"] = value }, cancellationToken);
        Assert.Equal($"current-calendar:{value}", JsonSerializer.SerializeToElement(result).GetString());
    }

    private static async Task AssertCompiledAgentflowToolCallAsync(
        IServiceProvider services,
        ExecutionRequest request,
        ExtensionCapabilitySnapshot capabilities,
        CancellationToken cancellationToken
    )
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var agentflow = await context.Agentflows.SingleAsync(
            item => item.Id == request.Target.AgentId,
            cancellationToken
        );
        var workflowFactory = scope.ServiceProvider.GetRequiredService<AgentflowRuntimeFactory>();
        var sessionScope = new AgentflowAgentSessionScope(
            scope.ServiceProvider.GetRequiredService<IProviderSessionState>(),
            request.Task.ProjectId,
            request.Task.ContextId,
            request.Task.TaskId,
            conversationId: request.Task.ProjectConversationId
        );
        await using var workflow = await workflowFactory.CreateAiWorkflow(
            agentflow,
            cancellationToken,
            sessionScope,
            environmentVariables: request.Settings.EnvironmentVariables,
            deferHumanInteractions: true,
            extensionPins: capabilities.Pins,
            acceptedExtensionCapabilities: capabilities
        );
        Assert.NotNull(workflow);
        var function = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(
                capabilities.SelectTools(
                    new ExtensionCapabilitySelection(
                        [new ExtensionToolSelection(capabilities.Pins.Single().ExtensionId, "calendar-lookup")],
                        []
                    )
                )
            )
        );
        var result = await function.InvokeAsync(new AIFunctionArguments { ["value"] = "agentflow" }, cancellationToken);
        Assert.Equal("current-calendar:agentflow", JsonSerializer.SerializeToElement(result).GetString());
    }

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

        public IServiceProvider Services => _services;

        public IProjectWorkspaceAccess WorkspaceAccess => _services.GetRequiredService<IProjectWorkspaceAccess>();

        public InProcessExecutionCoordinator Coordinator { get; }

        public static async Task<WorkspaceAuthorizationFixture> CreateAsync(string? extensionDirectory = null)
        {
            var token = TestContext.Current.CancellationToken;
            var root = Path.Combine(
                AppContext.BaseDirectory,
                ".scratch",
                "execution-workspace-authorization",
                Guid.CreateVersion7().ToString("N")
            );
            Directory.CreateDirectory(root);
            var values = new Dictionary<string, string?>
            {
                ["Database:Provider"] = "sqlite",
                ["Database:ConnectionString"] = $"Data Source={Path.Combine(root, "agw.db")};Pooling=False",
                ["Execution:Provider"] = "InProcess",
                ["Projects:Workspace:IsolationEnabled"] = "true",
                ["Agents:Shell:Backend"] = "local",
            };
            if (extensionDirectory != null)
                values["Extensions:Directory"] = extensionDirectory;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
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
                .AddTestExtensions(configuration)
                .AddJobs()
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

        public async Task<Guid> SeedSystemAgentAsync(Guid extensionId)
        {
            using var user = EnterUser(UserId);
            await using var scope = _services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            var modelId = Guid.CreateVersion7();
            var providerId = Guid.CreateVersion7();
            var modelProviderId = Guid.CreateVersion7();
            var agentId = Guid.CreateVersion7();
            var now = TimeProvider.System.GetUtcNow();
            context.Models.Add(
                new AgwAiModel
                {
                    Id = modelId,
                    Name = "test-model",
                    MaxContextWindowTokens = 128_000,
                    MaxOutputTokens = 16_000,
                    CreateBy = UserId,
                }
            );
            context.Providers.Add(
                new Provider
                {
                    Id = providerId,
                    Name = "test-provider",
                    ProviderType = ProviderType.OpenAIChatCompletions,
                    Endpoint = "https://example.test/v1",
                    CreateBy = UserId,
                    AuthConfigs =
                    [
                        new ProviderAuthConfig
                        {
                            Id = Guid.CreateVersion7(),
                            ProviderId = providerId,
                            AuthType = ProviderAuthType.ApiKey,
                            ApiKey = "test-api-key",
                            Enable = true,
                            CreateBy = UserId,
                        },
                    ],
                }
            );
            context.ModelProviders.Add(
                new ModelProviderRelation
                {
                    Id = modelProviderId,
                    ModelId = modelId,
                    ProviderId = providerId,
                    CreateBy = UserId,
                }
            );
            context.Agents.Add(
                new Agent
                {
                    Id = agentId,
                    Name = "compiled-extension-agent",
                    DisplayName = "Compiled Extension Agent",
                    Type = AgentType.System,
                    ModelProviderId = modelProviderId,
                    Tools =
                    [
                        new ToolValue
                        {
                            Definition = new ExtensionToolDefinition
                            {
                                ExtensionId = extensionId,
                                ToolId = "calendar-lookup",
                            },
                        },
                    ],
                    CreateBy = UserId,
                    CreateTime = now,
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return agentId;
        }

        public async Task<Guid> SeedAgentflowWithAgentAsync(Guid agentId)
        {
            using var user = EnterUser(UserId);
            await using var scope = _services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            var agentflowId = Guid.CreateVersion7();
            var now = TimeProvider.System.GetUtcNow();
            context.Agentflows.Add(
                new Agentflow
                {
                    Id = agentflowId,
                    Name = "compiled-extension-agentflow",
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
                            NodeId = "agent",
                            Kind = AgentflowNodeKind.Agent,
                            Name = "Agent",
                            RelateId = agentId,
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
                            EdgeId = "input-agent",
                            SourceNodeId = "input",
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
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return agentflowId;
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

        public Task<ExecutionRequest> AcceptAgentflowTurnAsync(WorkspaceSeed seed) =>
            AcceptAgentflowTurnAsync(seed, seed.AgentflowId);

        public async Task<ExecutionRequest> AcceptAgentflowTurnAsync(WorkspaceSeed seed, Guid agentflowId)
        {
            using var user = EnterUser(UserId);
            await using var scope = _services.CreateAsyncScope();
            var accepted = await scope
                .ServiceProvider.GetRequiredService<TurnAcceptanceService>()
                .AcceptAsync(
                    new TurnAcceptanceRequest(
                        UserId,
                        TurnId: null,
                        new ExecutionTarget(agentflowId, AgentRuntimeType.Agentflow),
                        seed.ConversationId,
                        TurnPersistenceTestKit.CreateInput("continue"),
                        SettingCommandMapper.FromCommand(new SettingCommand(seed.Project.Id, seed.ConversationId)),
                        Stream: false
                    ),
                    TestContext.Current.CancellationToken
                );
            return accepted.Request;
        }

        public async Task<ExecutionRequest> AcceptAgentTurnAsync(WorkspaceSeed seed, Guid agentId)
        {
            using var user = EnterUser(UserId);
            await using var scope = _services.CreateAsyncScope();
            var accepted = await scope
                .ServiceProvider.GetRequiredService<TurnAcceptanceService>()
                .AcceptAsync(
                    new TurnAcceptanceRequest(
                        UserId,
                        TurnId: null,
                        new ExecutionTarget(agentId, AgentRuntimeType.Agent),
                        seed.ConversationId,
                        TurnPersistenceTestKit.CreateInput("use compiled extension"),
                        SettingCommandMapper.FromCommand(new SettingCommand(seed.Project.Id, seed.ConversationId)),
                        Stream: true
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
