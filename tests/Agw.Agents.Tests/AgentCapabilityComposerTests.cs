using System.Diagnostics;
using System.Reflection;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.AIContextProviders.AgwWorkspace;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Integrations.Mcp;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Shared.Tooling;
using Agw.Tools.ContextualTools.WebSearch;
using Agw.Tools.Runtime;
using Agw.Tools.ToolBlocks;
using Agw.Tools.ToolBlocks.Blocks.UserMemory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentCapabilityComposerTests
{
    [Fact]
    public async Task ComposeAsync_LocalWebSearch_RegistersInvocationWarning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var toolRegistry = new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            serviceProvider,
            [new WebSearchContextualTool()],
            new ToolBlockRegistry([])
        );
        var composer = CreateComposer(
            database.Context,
            toolRegistry,
            new StubConnectionCapabilityResolver((_, _, _) => CreateResolution()),
            new StubMcpToolMaterializer((_, _, _) => CreateToolLease([], []))
        );
        var agent = new Agent
        {
            Type = AgentType.System,
            Tools = [new ToolValue { Definition = new WebSearchToolDefinition() }],
        };

        await using var composition = await composer.ComposeAsync(
            agent,
            new Project { Id = Guid.CreateVersion7() },
            new Dictionary<string, string>(),
            cancellationToken,
            supportsHostedWebSearch: false
        );

        Assert.Empty(composition.ToolWarnings);
        Assert.Equal(["web_search"], composition.PlanModeAllowedToolNames);
        var warning = Assert.Single(composition.ToolInvocationWarnings);
        Assert.Equal("web_search", warning.Key);
        Assert.Contains("using local search", warning.Value);
    }

    [Fact]
    public async Task ComposeAsync_SystemAgent_DedupesConnectionsAndMergesAllCapabilitySources()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var mcpServerId = Guid.CreateVersion7();
        database.Context.McpToolServers.Add(
            new McpServer
            {
                Id = mcpServerId,
                Name = "independent",
                TransportType = "stdio",
                Command = "server-command",
                EnvironmentVariables = new Dictionary<string, string> { ["CONFIGURED"] = "configured" },
                CreateBy = "tester",
            }
        );
        await database.Context.SaveChangesAsync(cancellationToken);

        var firstConnectionId = Guid.CreateVersion7();
        var secondConnectionId = Guid.CreateVersion7();
        var connectionResource = new TrackingResource();
        var resolver = new StubConnectionCapabilityResolver(
            (_, _, _) =>
                CreateResolution(
                    nativeTools: [CreateTool("work__repo"), CreateTool("personal__repo")],
                    warnings:
                    [
                        new ConnectionCapabilityWarning
                        {
                            Code = ConnectionCapabilityWarningCodes.ConnectionUnverified,
                            ConnectionId = secondConnectionId,
                            Message = "Connection is not verified.",
                        },
                    ],
                    resource: connectionResource
                )
        );
        var mcpResource = new TrackingResource();
        var materializer = new StubMcpToolMaterializer(
            (_, _, _) => CreateToolLease([CreateTool("independent_tool")], [mcpResource])
        );
        var composer = CreateComposer(database.Context, CreateToolRegistry(), resolver, materializer);
        var agent = new Agent
        {
            Type = AgentType.System,
            Tools = [new ToolValue { Definition = new DiffToolDefinition() }],
            AgentConnectionRelations =
            [
                new AgentConnectionRelation { ConnectionId = firstConnectionId },
                new AgentConnectionRelation { ConnectionId = secondConnectionId },
            ],
            AgentMcpToolServers = [new AgentMcpServerRelation { McpToolServerId = mcpServerId }],
        };
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            ProjectConnectionRelations = [new ProjectConnectionRelation { ConnectionId = firstConnectionId }],
            ProjectMcpToolServers = [new ProjectMcpServerRelation { McpToolServerId = mcpServerId }],
        };
        var runtimeEnvironment = new Dictionary<string, string> { ["RUNTIME"] = "runtime" };

        using var activity = new Activity("test").Start();
        var composition = await composer.ComposeAsync(agent, project, runtimeEnvironment, cancellationToken);

        Assert.Equal(
            new[] { firstConnectionId, secondConnectionId }.OrderBy(id => id),
            Assert.Single(resolver.Calls).ConnectionIds.OrderBy(id => id)
        );
        Assert.Equal(
            ["diff", "independent_tool", "personal__repo", "work__repo"],
            composition.Tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal)
        );
        Assert.Equal(["diff"], composition.PlanModeAllowedToolNames);
        Assert.Single(composition.Warnings);
        var warningEvent = Assert.Single(activity.Events, item => item.Name == "agw.integration.warning");
        Assert.Contains(
            warningEvent.Tags,
            tag =>
                tag.Key == "agw.integration.warning.code"
                && string.Equals(
                    tag.Value?.ToString(),
                    ConnectionCapabilityWarningCodes.ConnectionUnverified,
                    StringComparison.Ordinal
                )
        );
        Assert.Contains(
            warningEvent.Tags,
            tag =>
                tag.Key == "agw.integration.connection.id"
                && string.Equals(tag.Value?.ToString(), secondConnectionId.ToString(), StringComparison.Ordinal)
        );
        var materializeCall = Assert.Single(materializer.Calls);
        var descriptor = Assert.IsType<McpStdioEndpointDescriptor>(materializeCall.Descriptor);
        Assert.Equal("server-command", descriptor.Command);
        Assert.Equal("configured", descriptor.EnvironmentVariables["CONFIGURED"]);
        Assert.Equal("runtime", materializeCall.RuntimeOverrides!.EnvironmentVariables["RUNTIME"]);

        await composition.DisposeAsync();

        Assert.True(connectionResource.Disposed);
        Assert.True(mcpResource.Disposed);
    }

    [Fact]
    public async Task ComposeAsync_ExternalAgent_DoesNotResolveConnectionOrMcpCapabilities()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var resolver = new StubConnectionCapabilityResolver(
            (_, _, _) => throw new InvalidOperationException("resolver must not be called")
        );
        var materializer = new StubMcpToolMaterializer(
            (_, _, _) => throw new InvalidOperationException("materializer must not be called")
        );
        var composer = CreateComposer(database.Context, CreateToolRegistry(), resolver, materializer);
        var agent = new Agent
        {
            Type = AgentType.External,
            AgentConnectionRelations = [new AgentConnectionRelation { ConnectionId = Guid.CreateVersion7() }],
        };

        await using var composition = await composer.ComposeAsync(
            agent,
            new Project { Id = Guid.CreateVersion7() },
            new Dictionary<string, string>(),
            cancellationToken
        );

        Assert.Empty(composition.Tools);
        Assert.Empty(composition.ContextProviders);
        Assert.Empty(composition.PlanModeAllowedToolNames);
        Assert.Empty(resolver.Calls);
        Assert.Empty(materializer.Calls);
    }

    [Fact]
    public async Task ComposeAsync_ExternalAgent_UserMemoryConfigured_ContributesOnlyUserMemoryContext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var userMemoryBlock = new UserMemoryToolBlock(serviceProvider.GetRequiredService<IServiceScopeFactory>());
        var unrelatedBlock = new TestToolBlock();
        var resolver = new StubConnectionCapabilityResolver(
            (_, _, _) => throw new InvalidOperationException("resolver must not be called")
        );
        var materializer = new StubMcpToolMaterializer(
            (_, _, _) => throw new InvalidOperationException("materializer must not be called")
        );
        var composer = CreateComposer(
            database.Context,
            CreateToolRegistry(),
            resolver,
            materializer,
            [userMemoryBlock, unrelatedBlock]
        );
        var agent = new Agent
        {
            Type = AgentType.External,
            AgentConnectionRelations = [new AgentConnectionRelation { ConnectionId = Guid.CreateVersion7() }],
        };
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Tools =
            [
                new ToolBlockValue { Definition = new UserMemoryToolBlockDefinition() },
                new ToolBlockValue { Definition = new TestToolBlockDefinition() },
            ],
            ProjectMcpToolServers = [new ProjectMcpServerRelation { McpToolServerId = Guid.CreateVersion7() }],
        };

        await using var composition = await composer.ComposeAsync(
            agent,
            project,
            new Dictionary<string, string>(),
            cancellationToken
        );

        Assert.Empty(composition.Tools);
        Assert.IsType<UserMemoryProvider>(Assert.Single(composition.ContextProviders));
        Assert.Empty(composition.PlanModeAllowedToolNames);
        Assert.Null(unrelatedBlock.MaterializedDefaultMode);
        Assert.Empty(resolver.Calls);
        Assert.Empty(materializer.Calls);
    }

    [Fact]
    public async Task ComposeAsync_ExternalAgent_UnrelatedInvalidToolValues_DoNotBlockUserMemory()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var userMemoryBlock = new UserMemoryToolBlock(serviceProvider.GetRequiredService<IServiceScopeFactory>());
        var composer = CreateComposer(
            database.Context,
            CreateToolRegistry(),
            new StubConnectionCapabilityResolver((_, _, _) => CreateResolution()),
            new StubMcpToolMaterializer((_, _, _) => CreateToolLease([], [])),
            [userMemoryBlock]
        );
        var agent = new Agent { Type = AgentType.External };
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Tools =
            [
                new ToolBlockValue { Definition = new UserMemoryToolBlockDefinition() },
                new ToolBlockValue { Definition = new BackgroundAgentsToolBlockDefinition() },
                new ToolBlockValue { Definition = new TestToolBlockDefinition() },
                new ToolBlockValue { Definition = new TestToolBlockDefinition() },
                new ToolBlockValue { Definition = null! },
            ],
        };

        // Act
        await using var composition = await composer.ComposeAsync(
            agent,
            project,
            new Dictionary<string, string>(),
            cancellationToken
        );

        // Assert
        Assert.IsType<UserMemoryProvider>(Assert.Single(composition.ContextProviders));
    }

    [Fact]
    public async Task ComposeAsync_NonReadyConnection_ContributesWarningButNoTools()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var connectionId = Guid.CreateVersion7();
        var resolver = new StubConnectionCapabilityResolver(
            (_, _, _) =>
                CreateResolution(
                    warnings:
                    [
                        new ConnectionCapabilityWarning
                        {
                            Code = ConnectionCapabilityWarningCodes.ConnectionPendingAuthorization,
                            ConnectionId = connectionId,
                            Message = "Connection authorization is pending.",
                        },
                    ]
                )
        );
        var composer = CreateComposer(
            database.Context,
            CreateToolRegistry(),
            resolver,
            new StubMcpToolMaterializer((_, _, _) => CreateToolLease([], []))
        );
        var agent = new Agent
        {
            Type = AgentType.System,
            AgentConnectionRelations = [new AgentConnectionRelation { ConnectionId = connectionId }],
        };

        await using var composition = await composer.ComposeAsync(
            agent,
            new Project { Id = Guid.CreateVersion7() },
            new Dictionary<string, string>(),
            cancellationToken
        );

        Assert.Empty(composition.Tools);
        var warning = Assert.Single(composition.Warnings);
        Assert.Equal(ConnectionCapabilityWarningCodes.ConnectionPendingAuthorization, warning.Code);
        Assert.Equal(connectionId, warning.ConnectionId);
    }

    [Fact]
    public async Task ComposeAsync_CrossSourceToolNameConflict_ThrowsAndReleasesResolvedResources()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var resource = new TrackingResource();
        var resolver = new StubConnectionCapabilityResolver(
            (_, _, _) => CreateResolution(nativeTools: [CreateTool(ToolDefinitionNames.Diff)], resource: resource)
        );
        var composer = CreateComposer(
            database.Context,
            CreateToolRegistry(),
            resolver,
            new StubMcpToolMaterializer((_, _, _) => CreateToolLease([], []))
        );
        var agent = new Agent
        {
            Type = AgentType.System,
            Tools = [new ToolValue { Definition = new DiffToolDefinition() }],
            AgentConnectionRelations = [new AgentConnectionRelation { ConnectionId = Guid.CreateVersion7() }],
        };

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            composer.ComposeAsync(
                agent,
                new Project { Id = Guid.CreateVersion7() },
                new Dictionary<string, string>(),
                cancellationToken
            )
        );

        Assert.Equal(ErrorCodes.IntegrationToolNameConflict.Code, exception.Code);
        Assert.True(resource.Disposed);
    }

    [Fact]
    public async Task ComposeAsync_ToolBlockContribution_MaterializesMemberTool()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var toolBlock = new TestToolBlock();
        var composer = CreateComposer(
            database.Context,
            CreateToolRegistry(),
            new StubConnectionCapabilityResolver((_, _, _) => CreateResolution()),
            new StubMcpToolMaterializer((_, _, _) => CreateToolLease([], [])),
            [toolBlock]
        );
        var agent = new Agent
        {
            Type = AgentType.System,
            Tools = [new ToolBlockValue { Definition = new TestToolBlockDefinition() }],
        };

        await using var composition = await composer.ComposeAsync(
            agent,
            new Project { Id = Guid.CreateVersion7() },
            new Dictionary<string, string>(),
            cancellationToken
        );

        Assert.Equal("test_block_tool", Assert.Single(composition.Tools).Name);
        Assert.Equal(["test_block_tool"], composition.PlanModeAllowedToolNames);
        Assert.Equal("execute", toolBlock.MaterializedDefaultMode);
    }

    private static AgentCapabilityComposer CreateComposer(
        AgwDbContext dbContext,
        ToolRegistryService toolRegistry,
        IConnectionCapabilityResolver resolver,
        IMcpToolMaterializer materializer,
        IEnumerable<IToolBlock>? toolBlocks = null
    )
    {
        return new AgentCapabilityComposer(
            CreateAgentAppService(dbContext),
            toolRegistry,
            resolver,
            materializer,
            new ToolBlockRegistry(toolBlocks ?? []),
            NullLogger<AgentCapabilityComposer>.Instance,
            [new ProjectInstructionsSource()]
        );
    }

    private static AgentAppService CreateAgentAppService(AgwDbContext dbContext)
    {
        var connectionRepository = new EfRepository<Connection>(dbContext);
        var modelProviderRepository = new EfRepository<ModelProviderRelation>(dbContext);
        var modelRepository = new EfRepository<AgwAiModel>(dbContext);
        var providerRepository = new EfRepository<Provider>(dbContext);
        var skillRepository = new EfRepository<Skill>(dbContext);
        var userInfo = new TestUserInfoService();
        return new AgentAppService(
            dbContext,
            new TestConnectionReferenceFacade(connectionRepository, userInfo),
            new TestModelProviderReferenceFacade(
                modelProviderRepository,
                modelRepository,
                providerRepository,
                userInfo
            ),
            new TestSkillReferenceFacade(skillRepository, userInfo),
            userInfo,
            new Agw.Infrastructure.Agents.AgentDeletionCoordinator(
                dbContext,
                Agw.Shared.Coordination.InMemoryApplicationLock.Shared
            )
        );
    }

    private static ToolRegistryService CreateToolRegistry()
    {
        return new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            new ServiceCollection().BuildServiceProvider()
        );
    }

    private static AITool CreateTool(string name)
    {
        return AIFunctionFactory.Create((Func<string>)(() => name), new AIFunctionFactoryOptions { Name = name });
    }

    private static ConnectionCapabilityResolution CreateResolution(
        IReadOnlyList<AITool>? nativeTools = null,
        IReadOnlyList<AITool>? mcpTools = null,
        IReadOnlyList<PluginSkillReference>? pluginSkills = null,
        IReadOnlyList<ConnectionCapabilityWarning>? warnings = null,
        IAsyncDisposable? resource = null
    )
    {
        var lease = new ConnectionCapabilityLease();
        if (resource != null)
        {
            typeof(ConnectionCapabilityLease)
                .GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(lease, [resource]);
        }

        return (ConnectionCapabilityResolution)
            Activator.CreateInstance(
                typeof(ConnectionCapabilityResolution),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args:
                [
                    nativeTools ?? [],
                    mcpTools ?? [],
                    Array.Empty<ResolvedMcpCapabilitySource>(),
                    pluginSkills ?? [],
                    warnings ?? [],
                    lease,
                ],
                culture: null
            )!;
    }

    private static ConnectionToolLease CreateToolLease(
        IReadOnlyList<AITool> tools,
        IReadOnlyList<IAsyncDisposable> resources
    )
    {
        return (ConnectionToolLease)
            Activator.CreateInstance(
                typeof(ConnectionToolLease),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [tools, resources],
                culture: null
            )!;
    }

    private sealed class StubConnectionCapabilityResolver : IConnectionCapabilityResolver
    {
        private readonly Func<
            Guid,
            IReadOnlyCollection<Guid>,
            CancellationToken,
            ConnectionCapabilityResolution
        > _resolve;

        public StubConnectionCapabilityResolver(
            Func<Guid, IReadOnlyCollection<Guid>, CancellationToken, ConnectionCapabilityResolution> resolve
        )
        {
            _resolve = resolve;
        }

        public List<ResolverCall> Calls { get; } = [];

        public Task<ConnectionCapabilityResolution> ResolveAsync(
            Guid projectId,
            IReadOnlyCollection<Guid> connectionIds,
            CancellationToken cancellationToken
        )
        {
            Calls.Add(new ResolverCall(projectId, connectionIds.ToArray()));
            return Task.FromResult(_resolve(projectId, connectionIds, cancellationToken));
        }
    }

    private sealed class StubMcpToolMaterializer : IMcpToolMaterializer
    {
        private readonly Func<
            McpEndpointDescriptor,
            McpRuntimeOverrides?,
            CancellationToken,
            ConnectionToolLease
        > _materialize;

        public StubMcpToolMaterializer(
            Func<McpEndpointDescriptor, McpRuntimeOverrides?, CancellationToken, ConnectionToolLease> materialize
        )
        {
            _materialize = materialize;
        }

        public List<MaterializeCall> Calls { get; } = [];

        public Task<ConnectionToolLease> MaterializeAsync(
            McpEndpointDescriptor descriptor,
            McpRuntimeOverrides? runtimeOverrides = null,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add(new MaterializeCall(descriptor, runtimeOverrides));
            return Task.FromResult(_materialize(descriptor, runtimeOverrides, cancellationToken));
        }
    }

    private sealed class TrackingResource : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed record TestToolBlockDefinition : ToolBlockDefinition<EmptyToolOptions>
    {
        public override string GetDefinitionName() => "test-block";
    }

    private sealed class TestToolBlock : IToolBlock
    {
        public string? MaterializedDefaultMode { get; private set; }

        public ToolBlockDescriptor Descriptor { get; } =
            new(
                "test-block",
                "Test Block",
                "Contributes one test tool.",
                ToolBlockScope.Agent | ToolBlockScope.Project,
                ["test_block_tool"]
            );

        public ValueTask<ToolContribution> MaterializeAsync(
            ToolBlockDefinition definition,
            ToolMaterializationContext context,
            CancellationToken cancellationToken
        )
        {
            MaterializedDefaultMode = context.DefaultMode;
            var contribution = new ToolContribution();
            contribution.Tools.Add(CreateTool("test_block_tool"));
            contribution.PlanModeAllowedToolNames.Add("test_block_tool");
            return ValueTask.FromResult(contribution);
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(SqliteConnection connection, AgwDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public AgwDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed record ResolverCall(Guid ProjectId, IReadOnlyCollection<Guid> ConnectionIds);

    private sealed record MaterializeCall(McpEndpointDescriptor Descriptor, McpRuntimeOverrides? RuntimeOverrides);
}
