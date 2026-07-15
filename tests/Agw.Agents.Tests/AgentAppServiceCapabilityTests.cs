using System.Linq.Expressions;
using System.Reflection;

using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.ExternalAgents;
using Agw.Domain.Services;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;

using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentAppServiceCapabilityTests
{
    [Fact]
    public async Task CollectNamedToolNamesAsync_AgentAndProjectSources_UnionsAndDeduplicatesNames()
    {
        var agentAppId = Guid.NewGuid();
        var projectAppId = Guid.NewGuid();
        var service = CreateService(
            appInstances:
            [
                new AppInstance { Id = agentAppId, AppName = "agent-app" },
                new AppInstance { Id = projectAppId, AppName = "project-app" },
            ],
            appDefinitions:
            [
                CreateAppDefinition("agent-app", ["agent_app", "app_shared"]),
                CreateAppDefinition("project-app", ["APP_SHARED", "project_app"]),
            ]);

        var names = await service.CollectNamedToolNamesAsync(
            new string?[] { """["agent_direct","Shared"]""", """["project_direct","shared"]""" },
            new[] { agentAppId, projectAppId, agentAppId });

        Assert.Equal(
            ["agent_app", "agent_direct", "app_shared", "project_app", "project_direct", "Shared"],
            names);
    }

    [Fact]
    public async Task ListEnabledMcpToolServersAsync_AgentAndProjectIds_DeduplicatesAndFiltersDisabledServers()
    {
        var agentServerId = Guid.NewGuid();
        var projectServerId = Guid.NewGuid();
        var disabledServerId = Guid.NewGuid();
        var service = CreateService(
            mcpServers:
            [
                new McpServer { Id = agentServerId, Name = "agent", Enabled = true },
                new McpServer { Id = projectServerId, Name = "project", Enabled = true },
                new McpServer { Id = disabledServerId, Name = "disabled", Enabled = false },
            ]);

        var servers = await service.ListEnabledMcpToolServersAsync(new[]
        {
            agentServerId,
            projectServerId,
            agentServerId,
            disabledServerId,
        });

        Assert.Equal(
            new[] { agentServerId, projectServerId }.OrderBy(id => id).ToArray(),
            servers.Select(server => server.Id).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task ListSkillsAsync_AgentAndProjectIds_DeduplicatesSkills()
    {
        var agentSkillId = Guid.NewGuid();
        var projectSkillId = Guid.NewGuid();
        var service = CreateService(
            skills:
            [
                new Skill { Id = agentSkillId, Name = "agent" },
                new Skill { Id = projectSkillId, Name = "project" },
            ]);

        var skills = await service.ListSkillsAsync(new[]
        {
            agentSkillId,
            projectSkillId,
            agentSkillId,
        });

        Assert.Equal(
            new[] { agentSkillId, projectSkillId }.OrderBy(id => id).ToArray(),
            skills.Select(skill => skill.Id).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task CollectNamedToolNamesAsync_AgentCompatibilityWrapper_UsesAgentRelations()
    {
        var agentId = Guid.NewGuid();
        var appInstanceId = Guid.NewGuid();
        var service = CreateService(
            appInstances: [new AppInstance { Id = appInstanceId, AppName = "agent-app" }],
            appDefinitions: [CreateAppDefinition("agent-app", ["agent_app"])],
            agentAppRelations:
            [
                new AgentAppRelation { AgentId = agentId, AppInstanceId = appInstanceId },
            ]);

        var names = await service.CollectNamedToolNamesAsync(agentId, """["agent_direct"]""");

        Assert.Equal(["agent_app", "agent_direct"], names);
    }

    [Fact]
    public async Task ListEnabledMcpToolServersByAgentAsync_AgentCompatibilityWrapper_UsesAgentRelations()
    {
        var agentId = Guid.NewGuid();
        var server = new McpServer { Id = Guid.NewGuid(), Name = "agent-mcp", Enabled = true };
        var service = CreateService(
            mcpServers: [server],
            agentMcpRelations:
            [
                new AgentMcpServerRelation { AgentId = agentId, McpToolServerId = server.Id },
            ]);

        var servers = await service.ListEnabledMcpToolServersByAgentAsync(agentId);

        Assert.Equal(server.Id, Assert.Single(servers).Id);
    }

    [Fact]
    public async Task ListSkillsByAgentAsync_AgentCompatibilityWrapper_UsesAgentRelations()
    {
        var agentId = Guid.NewGuid();
        var skill = new Skill { Id = Guid.NewGuid(), Name = "agent-skill" };
        var service = CreateService(
            skills: [skill],
            agentSkillRelations:
            [
                new AgentSkillRelation { AgentId = agentId, SkillId = skill.Id },
            ]);

        var skills = await service.ListSkillsByAgentAsync(agentId);

        Assert.Equal(skill.Id, Assert.Single(skills).Id);
    }

    [Fact]
    public async Task CollectNamedToolNamesAsync_NullAndMalformedLayers_ContinuesWithLaterLayerAndApps()
    {
        var appInstanceId = Guid.NewGuid();
        var service = CreateService(
            appInstances: [new AppInstance { Id = appInstanceId, AppName = "project-app" }],
            appDefinitions: [CreateAppDefinition("project-app", ["project_app"])]);

        var names = await service.CollectNamedToolNamesAsync(
            new string?[] { null, "{malformed", """["later_direct"]""" },
            [appInstanceId]);

        Assert.Equal(["later_direct", "project_app"], names);
    }

    [Fact]
    public async Task CreateAiAgentAsync_ExternalAgent_UsesProjectEnvironmentWithoutResolvingCapabilities()
    {
        var agentAppRelationRepository = new TestRepository<AgentAppRelation> { ThrowOnList = true };
        var agentMcpRelationRepository = new TestRepository<AgentMcpServerRelation> { ThrowOnList = true };
        var agentSkillRelationRepository = new TestRepository<AgentSkillRelation> { ThrowOnList = true };
        var appInstanceRepository = new TestRepository<AppInstance> { ThrowOnList = true };
        var appDefinitionRepository = new TestRepository<AppDefinition> { ThrowOnList = true };
        var mcpServerRepository = new TestRepository<McpServer> { ThrowOnList = true };
        var skillRepository = new TestRepository<Skill> { ThrowOnList = true };
        var appService = CreateService(
            agentAppRelationRepository: agentAppRelationRepository,
            agentMcpRelationRepository: agentMcpRelationRepository,
            agentSkillRelationRepository: agentSkillRelationRepository,
            appInstanceRepository: appInstanceRepository,
            appDefinitionRepository: appDefinitionRepository,
            mcpServerRepository: mcpServerRepository,
            skillRepository: skillRepository);
        var project = new Project
        {
            Id = Guid.NewGuid(),
            ExtraSetting = JsonUtil.Serialize(new ClaudeCodeAIAgentOptions
            {
                PermissionMode = PermissionMode.bypassPermissions,
            }),
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["SHARED"] = "project",
                ["PROJECT_ONLY"] = "project",
            },
            Tools = """["project_direct"]""",
            ProjectAppRelations =
            [
                new ProjectAppRelation { AppInstanceId = Guid.NewGuid() },
            ],
            ProjectMcpToolServers =
            [
                new ProjectMcpServerRelation { McpToolServerId = Guid.NewGuid() },
            ],
            ProjectSkillRelations =
            [
                new ProjectSkillRelation { SkillId = Guid.NewGuid() },
            ],
        };
        var forbiddenTool = new ResolutionGuardTool("project_direct");
        var toolRegistry = CreateToolRegistry();
        toolRegistry.RegisterTool(forbiddenTool);
        var service = CreateRuntimeService(
            appService,
            new TestProjectAppService(project),
            toolRegistry);
        var request = new CreateAiAgentRequest
        {
            Agent = new Agent
            {
                Id = Guid.NewGuid(),
                Name = AgentNames.ClaudeCode,
                Type = AgentType.External,
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["SHARED"] = "agent",
                    ["AGENT_ONLY"] = "agent",
                },
                AgentAppRelations =
                [
                    new AgentAppRelation { AppInstanceId = Guid.NewGuid() },
                ],
                AgentMcpToolServers =
                [
                    new AgentMcpServerRelation { McpToolServerId = Guid.NewGuid() },
                ],
                AgentSkillRelations =
                [
                    new AgentSkillRelation { SkillId = Guid.NewGuid() },
                ],
            },
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

        Assert.NotNull(method);
        var agentTask = Assert.IsType<Task<AIAgent?>>(method.Invoke(
            service,
            [request, TestContext.Current.CancellationToken]));
        var externalAgent = await agentTask;

        Assert.NotNull(externalAgent);
        var options = FindInObjectGraph<ClaudeCodeAIAgentOptions>(externalAgent!);
        Assert.NotNull(options.EnvironmentVariables);
        Assert.Equal("session", options.EnvironmentVariables["SHARED"]);
        Assert.Equal("agent", options.EnvironmentVariables["AGENT_ONLY"]);
        Assert.Equal("project", options.EnvironmentVariables["PROJECT_ONLY"]);
        Assert.Equal("session", options.EnvironmentVariables["SESSION_ONLY"]);
        Assert.Equal(0, agentAppRelationRepository.ListCallCount);
        Assert.Equal(0, agentMcpRelationRepository.ListCallCount);
        Assert.Equal(0, agentSkillRelationRepository.ListCallCount);
        Assert.Equal(0, appInstanceRepository.ListCallCount);
        Assert.Equal(0, appDefinitionRepository.ListCallCount);
        Assert.Equal(0, mcpServerRepository.ListCallCount);
        Assert.Equal(0, skillRepository.ListCallCount);
        Assert.Equal(1, forbiddenTool.ToAIToolCallCount);
    }

    private static AppDefinition CreateAppDefinition(string name, List<string> toolNames) =>
        new()
        {
            Name = name,
            DisplayName = name,
            Category = CategoryType.Other,
            Provider = "test",
            Description = "test",
            AuthUrl = "https://example.test/auth",
            TokenEndpoint = "https://example.test/token",
            Scopes = [],
            ToolNames = toolNames,
        };

    private static AgentAppService CreateService(
        IEnumerable<AppInstance>? appInstances = null,
        IEnumerable<AppDefinition>? appDefinitions = null,
        IEnumerable<McpServer>? mcpServers = null,
        IEnumerable<Skill>? skills = null,
        IEnumerable<AgentAppRelation>? agentAppRelations = null,
        IEnumerable<AgentMcpServerRelation>? agentMcpRelations = null,
        IEnumerable<AgentSkillRelation>? agentSkillRelations = null,
        IRepository<AgentAppRelation>? agentAppRelationRepository = null,
        IRepository<AgentMcpServerRelation>? agentMcpRelationRepository = null,
        IRepository<AgentSkillRelation>? agentSkillRelationRepository = null,
        IRepository<AppInstance>? appInstanceRepository = null,
        IRepository<AppDefinition>? appDefinitionRepository = null,
        IRepository<McpServer>? mcpServerRepository = null,
        IRepository<Skill>? skillRepository = null)
    {
        return new AgentAppService(
            new TestRepository<Agent>(),
            agentAppRelationRepository ?? new TestRepository<AgentAppRelation>(agentAppRelations),
            appInstanceRepository ?? new TestRepository<AppInstance>(appInstances),
            appDefinitionRepository ?? new TestRepository<AppDefinition>(appDefinitions),
            new TestRepository<ModelProviderRelation>(),
            new TestRepository<LlmModel>(),
            new TestRepository<Provider>(),
            mcpServerRepository ?? new TestRepository<McpServer>(mcpServers),
            agentMcpRelationRepository ?? new TestRepository<AgentMcpServerRelation>(agentMcpRelations),
            skillRepository ?? new TestRepository<Skill>(skills),
            agentSkillRelationRepository ?? new TestRepository<AgentSkillRelation>(agentSkillRelations),
            new TestUnitOfWork(),
            new AgentDomainService(TimeProvider.System));
    }

    private static AgentRuntimeService CreateRuntimeService(
        AgentAppService appService,
        IProjectAppService projectAppService,
        ToolRegistryService toolRegistry,
        AgwDataPaths? dataPaths = null)
    {
        return new AgentRuntimeService(
            appService,
            projectAppService,
            toolRegistry,
            chatHistoryProvider: null!,
            providerSessionState: null!,
            taskSessionBindingService: null!,
            dataPaths ?? AgwDataPaths.Resolve(Path.GetTempPath(), Path.GetTempPath()),
            fileSystemResolver: null!,
            sessionStateStore: null!,
            NullLogger<AgentRuntimeService>.Instance,
            new ObservabilityMiddleware(NullLogger<ObservabilityMiddleware>.Instance),
            new UsageTrackingMiddleware(
                providerSessionState: null!,
                usageRecorder: null!,
                NullLogger<UsageTrackingMiddleware>.Instance),
            summaryService: null!);
    }

    private static ToolRegistryService CreateToolRegistry(params string[] toolNames)
    {
        var registry = new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            new ServiceCollection().BuildServiceProvider());
        foreach (var toolName in toolNames)
        {
            registry.RegisterTool(new TestTool(toolName));
        }

        return registry;
    }

    private static T FindInObjectGraph<T>(object root) where T : class
    {
        var pending = new Queue<(object Value, int Depth)>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        pending.Enqueue((root, 0));

        while (pending.Count > 0)
        {
            var (value, depth) = pending.Dequeue();
            if (!visited.Add(value) || depth > 8)
            {
                continue;
            }

            if (value is T match)
            {
                return match;
            }

            for (var type = value.GetType(); type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var child = field.GetValue(value);
                    if (child != null && !child.GetType().IsValueType && child is not string)
                    {
                        pending.Enqueue((child, depth + 1));
                    }
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"Could not find {typeof(T).Name} in the agent object graph.");
    }

    private sealed class TestRepository<TEntity> : IRepository<TEntity> where TEntity : class
    {
        private readonly List<TEntity> _items;

        public TestRepository(IEnumerable<TEntity>? items = null)
        {
            _items = items?.ToList() ?? [];
        }

        public IQueryable<TEntity> Queryable => _items.AsQueryable();

        public bool ThrowOnList { get; init; }

        public int ListCallCount { get; private set; }

        public Task<TEntity?> GetByIdAsync(object id) => Task.FromResult<TEntity?>(null);

        public Task<TEntity?> SingleOrDefaultAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.AsQueryable().SingleOrDefault(predicate));

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
        {
            ListCallCount++;
            if (ThrowOnList)
            {
                throw new Xunit.Sdk.XunitException($"Unexpected {typeof(TEntity).Name} capability lookup.");
            }

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

        public Task AddAsync(TEntity entity)
        {
            _items.Add(entity);
            return Task.CompletedTask;
        }

        public void Update(TEntity entity)
        {
        }

        public void Remove(TEntity entity) => _items.Remove(entity);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync() => Task.FromResult(0);
    }

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

    private sealed class ResolutionGuardTool : IAgwTool
    {
        public ResolutionGuardTool(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public int ToAIToolCallCount { get; private set; }

        public AITool ToAITool()
        {
            ToAIToolCallCount++;
            if (ToAIToolCallCount > 1)
            {
                throw new Xunit.Sdk.XunitException($"External Agent unexpectedly resolved tool '{Name}'.");
            }

            return AIFunctionFactory.Create(
                (Func<string>)(() => Name),
                new AIFunctionFactoryOptions { Name = Name });
        }
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
