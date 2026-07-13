using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Domain.Services;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Integrations;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Runtime;

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
        var root = Path.Combine(Path.GetTempPath(), $"agw-system-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "agent-skill"));
        Directory.CreateDirectory(Path.Combine(root, "project-skill"));

        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(dbOptions);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        var modelProviderId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var agentAppInstanceId = Guid.NewGuid();
        var projectAppInstanceId = Guid.NewGuid();
        var agentMcpServerId = Guid.NewGuid();
        var projectMcpServerId = Guid.NewGuid();
        var agentSkillId = Guid.NewGuid();
        var projectSkillId = Guid.NewGuid();

        dbContext.Models.Add(new LlmModel { Id = modelId, Name = "test-model" });
        dbContext.Providers.Add(new Provider
        {
            Id = providerId,
            Name = "test-provider",
            ProviderType = ProviderType.OpenAI,
            Endpoint = "https://example.test/v1",
            AuthConfigs =
            [
                new ProviderAuthConfig
                {
                    Id = Guid.NewGuid(),
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
        dbContext.AppInstances.AddRange(
            new AppInstance
            {
                Id = agentAppInstanceId,
                AppName = "agent-app",
                ClientId = "agent-client",
                ClientSecret = "agent-secret",
            },
            new AppInstance
            {
                Id = projectAppInstanceId,
                AppName = "project-app",
                ClientId = "project-client",
                ClientSecret = "project-secret",
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
                ContentPath = "agent-skill",
            },
            new Skill
            {
                Id = projectSkillId,
                Name = "project-skill",
                Description = "project skill",
                ContentPath = "project-skill",
            });
        await dbContext.SaveChangesAsync(cancellationToken);

        var appDefinitions = new TestRepository<AppDefinition>(
        [
            CreateAppDefinition("agent-app", ["agent_app"]),
            CreateAppDefinition("project-app", ["project_app"]),
        ]);
        var agentAppService = CreateAgentAppService(dbContext, appDefinitions);
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Tools = """["project_direct","shared"]""",
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["SHARED"] = "project",
                ["PROJECT_ONLY"] = "project",
            },
            ProjectAppRelations =
            [
                new ProjectAppRelation { AppInstanceId = projectAppInstanceId },
            ],
            ProjectMcpToolServers =
            [
                new ProjectMcpServerRelation { McpToolServerId = projectMcpServerId },
            ],
            ProjectSkillRelations =
            [
                new ProjectSkillRelation { SkillId = projectSkillId },
            ],
        };
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "system-agent",
            DisplayName = "System Agent",
            Type = AgentType.System,
            ModelProviderId = modelProviderId,
            Tools = """["agent_direct","Shared"]""",
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["SHARED"] = "agent",
                ["AGENT_ONLY"] = "agent",
            },
            AgentAppRelations =
            [
                new AgentAppRelation { AppInstanceId = agentAppInstanceId },
            ],
            AgentMcpToolServers =
            [
                new AgentMcpServerRelation { McpToolServerId = agentMcpServerId },
            ],
            AgentSkillRelations =
            [
                new AgentSkillRelation { SkillId = agentSkillId },
            ],
        };
        var toolRegistry = CreateToolRegistry(
            "agent_app",
            "agent_direct",
            "project_app",
            "project_direct",
            "Shared");
        var mcpCalls = new List<McpListCall>();
        Task<IReadOnlyList<AITool>> ListMcpToolsAsync(
            McpServer server,
            IReadOnlyDictionary<string, string> environmentVariables,
            CancellationToken _)
        {
            mcpCalls.Add(new McpListCall(
                server.Id,
                new Dictionary<string, string>(environmentVariables, StringComparer.Ordinal)));
            return Task.FromResult<IReadOnlyList<AITool>>([new TestTool(server.Name).ToAITool()]);
        }

        var runtimeService = CreateRuntimeService(
            agentAppService,
            new TestProjectAppService(project),
            toolRegistry,
            AgwDataPaths.Resolve(root, root),
            ListMcpToolsAsync);
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
            Assert.NotNull(chatOptions.Tools);
            Assert.Equal(
                new[]
                {
                    "Shared",
                    "agent_app",
                    "agent_direct",
                    "agent_mcp",
                    "project_app",
                    "project_direct",
                    "project_mcp",
                }.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                chatOptions.Tools
                    .Select(tool => tool.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());

            Assert.Equal(2, mcpCalls.Count);
            Assert.Equal(
                new[] { agentMcpServerId, projectMcpServerId }.OrderBy(id => id).ToArray(),
                mcpCalls.Select(call => call.ServerId).OrderBy(id => id).ToArray());
            foreach (var call in mcpCalls)
            {
                Assert.Equal("session", call.EnvironmentVariables["SHARED"]);
                Assert.Equal("agent", call.EnvironmentVariables["AGENT_ONLY"]);
                Assert.Equal("project", call.EnvironmentVariables["PROJECT_ONLY"]);
                Assert.Equal("session", call.EnvironmentVariables["SESSION_ONLY"]);
            }

            Assert.NotNull(agentOptions.AIContextProviders);
            var skillsProvider = Assert.Single(agentOptions.AIContextProviders.OfType<AgentSkillsProvider>());
            var providerStrings = CollectStringsInObjectGraph(skillsProvider);
            Assert.Contains(Path.Combine(root, "agent-skill"), providerStrings);
            Assert.Contains(Path.Combine(root, "project-skill"), providerStrings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentAppService CreateAgentAppService(
        AgwDbContext dbContext,
        IRepository<AppDefinition> appDefinitionRepository)
    {
        return new AgentAppService(
            new EfRepository<Agent>(dbContext),
            new EfRepository<AgentAppRelation>(dbContext),
            new EfRepository<AppInstance>(dbContext),
            appDefinitionRepository,
            new EfRepository<ModelProviderRelation>(dbContext),
            new EfRepository<LlmModel>(dbContext),
            new EfRepository<Provider>(dbContext),
            new EfRepository<McpServer>(dbContext),
            new EfRepository<AgentMcpServerRelation>(dbContext),
            new EfRepository<Skill>(dbContext),
            new EfRepository<AgentSkillRelation>(dbContext),
            new UnitOfWork(dbContext),
            new AgentDomainService(TimeProvider.System));
    }

    private static AgentRuntimeService CreateRuntimeService(
        AgentAppService appService,
        IProjectAppService projectAppService,
        ToolRegistryService toolRegistry,
        AgwDataPaths dataPaths,
        Func<
            McpServer,
            IReadOnlyDictionary<string, string>,
            CancellationToken,
            Task<IReadOnlyList<AITool>>> mcpToolLister)
    {
        return new AgentRuntimeService(
            appService,
            projectAppService,
            toolRegistry,
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
            mcpToolLister);
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
        Guid ServerId,
        IReadOnlyDictionary<string, string> EnvironmentVariables);

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
