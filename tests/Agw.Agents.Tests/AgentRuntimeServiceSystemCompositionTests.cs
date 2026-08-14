using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.AIContextProviders.AgwWorkspace;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Domain.Services;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Integrations.Application.Capabilities;
using Agw.Integrations.Mcp;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Entities.Tools;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Runtime;
using Agw.Skills.Application.Remote;
using Agw.Skills.Contracts.Registration;
using Agw.Skills.Execution;
using Agw.Tools.ToolBlocks;

using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentRuntimeServiceSystemCompositionTests
{
    [Fact]
    public async Task CreateAiAgentAsync_SystemAgent_ComposesProjectCapabilitiesAndPassesEffectiveEnvironmentToMcp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"agw-system-composition-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(Path.Combine(root, "agent-skill"));
        Directory.CreateDirectory(Path.Combine(root, "project-skill"));
        var overriddenPluginSkillDirectory = Path.Combine(root, "plugin-overridden-skill");
        var pluginSkillDirectory = Path.Combine(root, "plugin-skill");
        Directory.CreateDirectory(overriddenPluginSkillDirectory);
        Directory.CreateDirectory(pluginSkillDirectory);
        File.WriteAllText(Path.Combine(overriddenPluginSkillDirectory, "SKILL.md"), "# overridden");
        File.WriteAllText(Path.Combine(pluginSkillDirectory, "SKILL.md"), "# plugin");
        File.WriteAllText(Path.Combine(pluginSkillDirectory, "untrusted.py"), "print('do not run')");

        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(dbOptions);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        var modelProviderId = Guid.CreateVersion7();
        var modelId = Guid.CreateVersion7();
        var providerId = Guid.CreateVersion7();
        var agentMcpServerId = Guid.CreateVersion7();
        var projectMcpServerId = Guid.CreateVersion7();
        var agentSkillId = Guid.CreateVersion7();
        var projectSkillId = Guid.CreateVersion7();
        var remoteSkillId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var classSkillRegistration = new TestSkillRegistration();

        dbContext.Models.Add(new AgwAiModel { Id = modelId, Name = "test-model" });
        dbContext.Providers.Add(new Provider
        {
            Id = providerId,
            Name = "test-provider",
            ProviderType = ProviderType.OpenAIChatCompletions,
            Endpoint = "https://example.test/v1",
            AuthConfigs =
            [
                new ProviderAuthConfig
                {
                    Id = Guid.CreateVersion7(),
                    ProviderId = providerId,
                    AuthType = ProviderAuthType.ApiKey,
                    ApiKey = "test-api-key",
                    Enable = true,
                },
            ],
        });
        dbContext.ModelProviders.Add(new ModelProviderRelation
        {
            Id = modelProviderId,
            ModelId = modelId,
            ProviderId = providerId,
        });
        dbContext.McpToolServers.AddRange(
            new McpServer { Id = agentMcpServerId, Name = "agent_mcp", Enabled = true },
            new McpServer { Id = projectMcpServerId, Name = "project_mcp", Enabled = true });
        dbContext.Skills.AddRange(
            new Skill
            {
                Id = agentSkillId,
                Name = "agent-skill",
                Description = "agent skill",
                Kind = SkillKind.Local,
                ContentPath = "agent-skill",
            },
            new Skill
            {
                Id = projectSkillId,
                Name = "project-skill",
                Description = "project skill",
                Kind = SkillKind.Local,
                ContentPath = "project-skill",
            },
            new Skill
            {
                Id = remoteSkillId,
                Name = "remote-skill",
                Description = "remote skill",
                Kind = SkillKind.Remote,
                ContentPath = string.Empty,
                RemoteUrl = "https://example.test/remote-skill",
            },
            new Skill
            {
                Id = classSkillRegistration.Id,
                Name = classSkillRegistration.Name,
                Description = classSkillRegistration.Description,
                Kind = SkillKind.BuiltIn,
                ContentPath = string.Empty,
            });
        await dbContext.SaveChangesAsync(cancellationToken);

        var agentAppService = CreateAgentAppService(dbContext);
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Workspace = Path.Combine(root, "workspace"),
            Tools =
            [
                new ToolValue { Definition = new GenerateGuidToolDefinition() },
                new ToolValue { Definition = new WebFetchToolDefinition() }
            ],
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["SHARED"] = "project",
                ["PROJECT_ONLY"] = "project",
            },
            ProjectMcpToolServers =
            [
                new ProjectMcpServerRelation { McpToolServerId = projectMcpServerId },
            ],
            ProjectSkillRelations =
            [
                new ProjectSkillRelation { SkillId = projectSkillId },
                new ProjectSkillRelation { SkillId = remoteSkillId },
                new ProjectSkillRelation { SkillId = classSkillRegistration.Id },
            ],
            ProjectConnectionRelations =
            [
                new ProjectConnectionRelation { ConnectionId = connectionId },
            ],
        };
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            DisplayName = "System Agent",
            Type = AgentType.System,
            ModelProviderId = modelProviderId,
            Tools =
            [
                new ToolValue { Definition = new BashToolDefinition() },
                new ToolValue { Definition = new WebFetchToolDefinition() }
            ],
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["SHARED"] = "agent",
                ["AGENT_ONLY"] = "agent",
            },
            AgentMcpToolServers =
            [
                new AgentMcpServerRelation { McpToolServerId = agentMcpServerId },
            ],
            AgentSkillRelations =
            [
                new AgentSkillRelation { SkillId = agentSkillId },
                new AgentSkillRelation { SkillId = classSkillRegistration.Id },
            ],
            AgentConnectionRelations =
            [
                new AgentConnectionRelation { ConnectionId = connectionId },
            ],
        };
        var toolRegistry = CreateToolRegistry();
        var mcpMaterializer = new TestMcpToolMaterializer();
        var connectionResource = new TrackingResource();
        var remoteSkillResolver = new TestRemoteSkillContentResolver();
        var connectionResolver = new TestConnectionCapabilityResolver(CreateResolution(
            pluginSkills:
            [
                new PluginSkillReference
                {
                    PluginId = "github",
                    SkillId = "agent-skill",
                    Description = "overridden",
                    SkillFilePath = Path.Combine(overriddenPluginSkillDirectory, "SKILL.md"),
                },
                new PluginSkillReference
                {
                    PluginId = "github",
                    SkillId = "plugin-skill",
                    Description = "plugin",
                    SkillFilePath = Path.Combine(pluginSkillDirectory, "SKILL.md"),
                },
            ],
            resource: connectionResource));

        var runtimeService = CreateRuntimeService(
            agentAppService,
            new TestProjectAppService(project),
            toolRegistry,
            AgwDataPaths.Resolve(root, root),
            connectionResolver,
            mcpMaterializer,
            [classSkillRegistration],
            remoteSkillResolver);
        var request = new CreateAiAgentRequest
        {
            Agent = agent,
            ProjectId = project.Id,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["SHARED"] = "session",
                ["SESSION_ONLY"] = "session",
            },
        };
        var method = typeof(AgentRuntimeService).GetMethod(
            "CreateAiAgentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(CreateAiAgentRequest), typeof(CancellationToken)]);

        try
        {
            Assert.NotNull(method);
            var agentTask = Assert.IsType<Task<AIAgent?>>(method.Invoke(
                runtimeService,
                [request, cancellationToken]));
            var aiAgent = await agentTask;

            Assert.NotNull(aiAgent);
            var agentOptions = FindInObjectGraph<ChatClientAgentOptions>(aiAgent!);
            var chatOptions = Assert.IsType<ChatOptions>(agentOptions.ChatOptions);
            Assert.Equal("You are a helpful agent.", chatOptions.Instructions);
            Assert.NotNull(chatOptions.Tools);
            Assert.Equal(
                new[]
                {
                    "bash",
                    "agent_mcp",
                    "generate_guid",
                    "project_mcp",
                    "web_fetch"
                }.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                chatOptions.Tools
                    .Select(tool => tool.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());

            Assert.Equal(2, mcpMaterializer.Calls.Count);
            Assert.Equal(
                new[] { "agent_mcp", "project_mcp" },
                mcpMaterializer.Calls.Select(call => call.ServerName).OrderBy(name => name).ToArray());
            foreach (var call in mcpMaterializer.Calls)
            {
                Assert.Equal("session", call.EnvironmentVariables["SHARED"]);
                Assert.Equal("agent", call.EnvironmentVariables["AGENT_ONLY"]);
                Assert.Equal("project", call.EnvironmentVariables["PROJECT_ONLY"]);
                Assert.Equal("session", call.EnvironmentVariables["SESSION_ONLY"]);
            }

            Assert.NotNull(agentOptions.AIContextProviders);
            var contextProviders = agentOptions.AIContextProviders.ToArray();
            Assert.Equal(2, contextProviders.Length);
            var instructionsProvider = Assert.IsType<AgwWorkspaceProvider>(contextProviders[0]);
            var skillsProvider = Assert.IsType<AgentSkillsProvider>(contextProviders[1]);
            var instructionsContext = await instructionsProvider.InvokingAsync(
                new AIContextProvider.InvokingContext(aiAgent, null, new AIContext()),
                cancellationToken);
            Assert.Contains(project.Workspace, instructionsContext.Instructions, StringComparison.Ordinal);
            var providerStrings = CollectStringsInObjectGraph(skillsProvider);
            Assert.Contains(Path.Combine(root, "agent-skill"), providerStrings);
            Assert.Contains(Path.Combine(root, "project-skill"), providerStrings);
            Assert.Contains(pluginSkillDirectory, providerStrings);
            Assert.DoesNotContain(overriddenPluginSkillDirectory, providerStrings);
            Assert.Equal(1, classSkillRegistration.CreateCount);
            Assert.Single(TraverseObjectGraph(skillsProvider).OfType<TestClassSkill>());
            var remoteSkill = Assert.Single(
                TraverseObjectGraph(skillsProvider).OfType<RemoteAgentSkill>());
            var remoteContent = await remoteSkill.GetContentAsync(cancellationToken);
            Assert.Contains("remote instructions", remoteContent, StringComparison.Ordinal);
            Assert.Equal([remoteSkillId], remoteSkillResolver.ResolvedSkillIds);
            var pluginFileSource = TraverseObjectGraph(skillsProvider)
                .OfType<AgentFileSkillsSource>()
                .Single(source => CollectStringsInObjectGraph(source).Contains(pluginSkillDirectory));
            var pluginSourceStrings = CollectStringsInObjectGraph(pluginFileSource);
            Assert.DoesNotContain(".py", pluginSourceStrings);
            Assert.DoesNotContain(".js", pluginSourceStrings);
            Assert.DoesNotContain(".cs", pluginSourceStrings);
            var skillsInstructionPrompt = Assert.Single(
                providerStrings,
                value => value.Contains(
                    "Skill files are stored outside the project workspace.",
                    StringComparison.Ordinal));
            Assert.Contains("{skills}", skillsInstructionPrompt, StringComparison.Ordinal);
            Assert.Contains(AgentSkillsProvider.LoadSkillToolName, skillsInstructionPrompt, StringComparison.Ordinal);
            Assert.Contains(AgentSkillsProvider.ReadSkillResourceToolName, skillsInstructionPrompt, StringComparison.Ordinal);
            Assert.Contains(AgentSkillsProvider.RunSkillScriptToolName, skillsInstructionPrompt, StringComparison.Ordinal);
            Assert.Contains(
                "Never use bash, glob, ls, or project file tools to locate skill files.",
                skillsInstructionPrompt,
                StringComparison.Ordinal);
            Assert.Contains(
                "Do not search the project workspace.",
                skillsInstructionPrompt,
                StringComparison.Ordinal);
            Assert.Contains(".py", providerStrings);
            Assert.Contains(".js", providerStrings);
            Assert.Contains(".cs", providerStrings);
            Assert.DoesNotContain(".csx", providerStrings);
            Assert.DoesNotContain(".sh", providerStrings);
            Assert.DoesNotContain(".ps1", providerStrings);

            var approvalAgent = FindInObjectGraph<ToolApprovalAgent>(aiAgent!);
            var rulesField = typeof(ToolApprovalAgent).GetField(
                "_autoApprovalRules",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(rulesField);
            var approvalRules = Assert.IsAssignableFrom<
                IReadOnlyList<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>>(rulesField.GetValue(approvalAgent));
            Assert.Same(AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule, Assert.Single(approvalRules));
            Assert.DoesNotContain(ToolApprovalAgent.AllToolsAutoApprovalRule, approvalRules);

            await Assert.IsAssignableFrom<IAsyncDisposable>(aiAgent).DisposeAsync();
            Assert.True(connectionResource.Disposed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task CreateSkillsProviderAsync_ClassSkillSelection_UsesAgentOrProjectRelationOnce(
        bool agentSelected,
        bool projectSelected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"agw-class-skill-selection-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(root);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(dbOptions);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        var registration = new TestSkillRegistration();
        dbContext.Skills.Add(new Skill
        {
            Id = registration.Id,
            Name = registration.Name,
            Description = registration.Description,
            Kind = SkillKind.BuiltIn,
            ContentPath = string.Empty,
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Workspace = root,
            ProjectSkillRelations = projectSelected
                ? [new ProjectSkillRelation { SkillId = registration.Id }]
                : [],
        };
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            Type = AgentType.System,
            AgentSkillRelations = agentSelected
                ? [new AgentSkillRelation { SkillId = registration.Id }]
                : [],
        };
        var runtimeService = CreateRuntimeService(
            CreateAgentAppService(dbContext),
            new TestProjectAppService(project),
            CreateToolRegistry(),
            AgwDataPaths.Resolve(root, root),
            new TestConnectionCapabilityResolver(CreateResolution(
                [],
                new TrackingResource())),
            new TestMcpToolMaterializer(),
            [registration]);
        var method = typeof(AgentRuntimeService).GetMethod(
            "CreateSkillsProviderAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(Agent), typeof(Project), typeof(IReadOnlyList<PluginSkillReference>)]);

        try
        {
            Assert.NotNull(method);
            var providerTask = Assert.IsType<Task<AgentSkillsProvider?>>(method.Invoke(
                runtimeService,
                [agent, project, Array.Empty<PluginSkillReference>()]));
            var provider = await providerTask;

            if (!agentSelected && !projectSelected)
            {
                Assert.Null(provider);
                Assert.Equal(0, registration.CreateCount);
                return;
            }

            var skillsProvider = Assert.IsType<AgentSkillsProvider>(provider);
            Assert.Equal(1, registration.CreateCount);
            Assert.Single(TraverseObjectGraph(skillsProvider).OfType<TestClassSkill>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentAppService CreateAgentAppService(AgwDbContext dbContext)
    {
        return new AgentAppService(
            new EfRepository<Agent>(dbContext),
            new EfRepository<AgentConnectionRelation>(dbContext),
            new EfRepository<Connection>(dbContext),
            new EfRepository<ModelProviderRelation>(dbContext),
            new EfRepository<AgwAiModel>(dbContext),
            new EfRepository<Provider>(dbContext),
            new EfRepository<McpServer>(dbContext),
            new EfRepository<AgentMcpServerRelation>(dbContext),
            new EfRepository<Skill>(dbContext),
            new EfRepository<AgentSkillRelation>(dbContext),
            dbContext,
            new AgentDomainService(TimeProvider.System));
    }

    private static AgentRuntimeService CreateRuntimeService(
        AgentAppService appService,
        IProjectAppService projectAppService,
        ToolRegistryService toolRegistry,
        AgwDataPaths dataPaths,
        IConnectionCapabilityResolver connectionCapabilityResolver,
        IMcpToolMaterializer mcpToolMaterializer,
        IEnumerable<IAgentSkillRegistration>? skillRegistrations = null,
        IRemoteSkillContentResolver? remoteSkillContentResolver = null)
    {
        return new AgentRuntimeService(
            appService,
            projectAppService,
            new AgentCapabilityComposer(
                appService,
                toolRegistry,
                connectionCapabilityResolver,
                mcpToolMaterializer,
                new ToolBlockRegistry([]),
                NullLogger<AgentCapabilityComposer>.Instance,
                [new ProjectInstructionsSource()]),
            chatHistoryProvider: null!,
            providerSessionState: null!,
            taskSessionBindingService: null!,
            dataPaths,
            fileSystemResolver: null!,
            sessionStateStore: null!,
            NullLogger<AgentRuntimeService>.Instance,
            new ObservabilityMiddleware(NullLogger<ObservabilityMiddleware>.Instance),
            new UsageTrackingMiddleware(
                providerSessionState: null!,
                usageRecorder: null!,
                NullLogger<UsageTrackingMiddleware>.Instance),
            summaryService: null!,
            skillRegistrations: skillRegistrations,
            remoteSkillContentResolver: remoteSkillContentResolver);
    }

    private static ToolRegistryService CreateToolRegistry()
    {
        return new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            new ServiceCollection().BuildServiceProvider());
    }

    private static T FindInObjectGraph<T>(object root) where T : class
    {
        foreach (var value in TraverseObjectGraph(root))
        {
            if (value is T match)
            {
                return match;
            }
        }

        throw new Xunit.Sdk.XunitException($"Could not find {typeof(T).Name} in the agent object graph.");
    }

    private static IReadOnlySet<string> CollectStringsInObjectGraph(object root) =>
        TraverseObjectGraph(root).OfType<string>().ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<object> TraverseObjectGraph(object root)
    {
        var pending = new Queue<(object Value, int Depth)>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        pending.Enqueue((root, 0));

        while (pending.Count > 0)
        {
            var (value, depth) = pending.Dequeue();
            if (!visited.Add(value) || depth > 10)
            {
                continue;
            }

            yield return value;
            if (value is IEnumerable enumerable and not string)
            {
                foreach (var child in enumerable)
                {
                    if (child != null)
                    {
                        pending.Enqueue((child, depth + 1));
                    }
                }
            }

            for (var type = value.GetType(); type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var child = field.GetValue(value);
                    if (child != null && !child.GetType().IsValueType)
                    {
                        pending.Enqueue((child, depth + 1));
                    }
                }
            }
        }
    }

    private sealed record McpListCall(
        string ServerName,
        IReadOnlyDictionary<string, string> EnvironmentVariables);

    private sealed class TestMcpToolMaterializer : IMcpToolMaterializer
    {
        public List<McpListCall> Calls { get; } = [];

        public Task<ConnectionToolLease> MaterializeAsync(
            McpEndpointDescriptor descriptor,
            McpRuntimeOverrides? runtimeOverrides = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new McpListCall(
                descriptor.Name,
                new Dictionary<string, string>(
                    runtimeOverrides?.EnvironmentVariables ?? new Dictionary<string, string>(),
                    StringComparer.Ordinal)));
            var tools = new AITool[] { new TestTool(descriptor.Name).ToAITool() };
            var lease = (ConnectionToolLease)Activator.CreateInstance(
                typeof(ConnectionToolLease),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [tools, Array.Empty<IAsyncDisposable>()],
                culture: null)!;
            return Task.FromResult(lease);
        }
    }

    private sealed class TestConnectionCapabilityResolver : IConnectionCapabilityResolver
    {
        private readonly ConnectionCapabilityResolution _resolution;

        public TestConnectionCapabilityResolver(ConnectionCapabilityResolution resolution)
        {
            _resolution = resolution;
        }

        public Task<ConnectionCapabilityResolution> ResolveAsync(
            Guid projectId,
            IReadOnlyCollection<Guid> connectionIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(_resolution);
    }

    private static ConnectionCapabilityResolution CreateResolution(
        IReadOnlyList<PluginSkillReference> pluginSkills,
        IAsyncDisposable resource)
    {
        var lease = new ConnectionCapabilityLease();
        typeof(ConnectionCapabilityLease)
            .GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(lease, [resource]);
        return (ConnectionCapabilityResolution)Activator.CreateInstance(
            typeof(ConnectionCapabilityResolution),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                Array.Empty<AITool>(),
                Array.Empty<AITool>(),
                Array.Empty<ResolvedMcpCapabilitySource>(),
                pluginSkills,
                Array.Empty<ConnectionCapabilityWarning>(),
                lease,
            ],
            culture: null)!;
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

#pragma warning disable MAAI001

    private sealed class TestSkillRegistration : IAgentSkillRegistration
    {
        public Guid Id { get; } = Guid.Parse("11111111-1111-1111-8888-000000000002");

        public string Name => "agw-job";

        public string Description => "Manage jobs.";

        public int CreateCount { get; private set; }

        public AgentSkill Create(Guid projectId)
        {
            CreateCount++;
            return new TestClassSkill();
        }
    }

    private sealed class TestClassSkill : AgentClassSkill<TestClassSkill>
    {
        public override AgentSkillFrontmatter Frontmatter { get; } =
            new("agw-job", "Manage jobs.");

        protected override string Instructions => "Manage jobs in the current project.";
    }

    private sealed class TestRemoteSkillContentResolver : IRemoteSkillContentResolver
    {
        public List<Guid> ResolvedSkillIds { get; } = [];

        public Task<RemoteSkillDefinition> ResolveAsync(
            Guid skillId,
            CancellationToken cancellationToken = default)
        {
            ResolvedSkillIds.Add(skillId);
            return Task.FromResult(new RemoteSkillDefinition(
                "remote-skill",
                "remote skill",
                "remote instructions",
                ["remote"]));
        }
    }

#pragma warning restore MAAI001

    private sealed class TestTool : IAgwTool
    {
        public TestTool(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public AITool ToAITool() => AIFunctionFactory.Create(
            (Func<string>)(() => Name),
            new AIFunctionFactoryOptions { Name = Name });
    }

    private sealed class TestRepository<TEntity> : IRepository<TEntity> where TEntity : class
    {
        private readonly List<TEntity> _items;

        public TestRepository(IEnumerable<TEntity> items)
        {
            _items = items.ToList();
        }

        public IQueryable<TEntity> Queryable => _items.AsQueryable();

        public Task<TEntity?> GetByIdAsync(object id) => Task.FromResult<TEntity?>(null);

        public Task<TEntity?> SingleOrDefaultAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.AsQueryable().SingleOrDefault(predicate));

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
        {
            IQueryable<TEntity> query = _items.AsQueryable();
            if (predicate != null)
            {
                query = query.Where(predicate);
            }

            if (orderBy != null)
            {
                query = orderBy(query);
            }

            return Task.FromResult<IReadOnlyList<TEntity>>(query.ToList());
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy,
            params Expression<Func<TEntity, object>>[] includes) =>
            ListAsync(predicate, orderBy);

        public Task AddAsync(TEntity entity) => throw new NotSupportedException();

        public void Update(TEntity entity) => throw new NotSupportedException();

        public void Remove(TEntity entity) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestProjectAppService : IProjectAppService
    {
        private readonly Project _project;

        public TestProjectAppService(Project project)
        {
            _project = project;
        }

        public Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null) =>
            Task.FromResult<IReadOnlyList<Project>>([_project]);

        public Task<string?> GetProjectExtraSettingAsync(Guid? projectId) =>
            Task.FromResult(_project.ExtraSetting);

        public Task<Guid?> ResolveProjectIdAsync(Guid? projectId) => Task.FromResult<Guid?>(_project.Id);

        public Task<Project?> CreateAsync(Project project, string user) => Task.FromResult<Project?>(project);

        public Task<bool> DeleteAsync(Guid id) => Task.FromResult(false);

        public Task<Project?> GetAsync(Guid id) => Task.FromResult<Project?>(_project);

        public Task<Project?> UpdateAsync(Guid id, Action<Project> updateAction, string user) =>
            Task.FromResult<Project?>(_project);
    }
}
