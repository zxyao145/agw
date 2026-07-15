using System.Linq.Expressions;
using System.Text.Json;

using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Definitions.Controllers;
using Agw.Agents.ExternalAgents;
using Agw.Domain.Services;
using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentSuggestionAppServiceTests
{
    [Fact]
    public async Task GetSuggestionsAsync_SystemAgent_MergesDirectProjectAndAgentCapabilities()
    {
        var deploySkill = new Skill
        {
            Id = Guid.NewGuid(),
            Name = "deploy",
            Description = "Deploy the application",
        };
        var reviewSkill = new Skill
        {
            Id = Guid.NewGuid(),
            Name = "review",
        };
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "system-agent",
            Type = AgentType.System,
            Tools = """["Deploy","missing","broken"]""",
            AgentSkillRelations =
            [
                new AgentSkillRelation { SkillId = deploySkill.Id },
                new AgentSkillRelation { SkillId = reviewSkill.Id },
            ],
        };
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Tools = """["deploy","search"]""",
            ProjectSkillRelations =
            [
                new ProjectSkillRelation { SkillId = deploySkill.Id },
            ],
        };
        var service = CreateService(
            agents: [agent],
            projects: [project],
            skills: [deploySkill, reviewSkill],
            tools:
            [
                new TestTool("deploy", "Operations", "Runs deployment"),
                new TestTool("search", "Knowledge", string.Empty),
            ]);

        var response = await service.GetSuggestionsAsync(project.Id, agent.Id);

        Assert.Equal(AgentSuggestionMode.System, response.Mode);
        Assert.Equal(
        [
            new AgentSuggestionResponse("/deploy", "Skill · Deploy the application", AgentSuggestionKind.Skill),
            new AgentSuggestionResponse("/deploy", "Tool · Operations · Runs deployment", AgentSuggestionKind.Tool),
            new AgentSuggestionResponse("/review", "Skill", AgentSuggestionKind.Skill),
            new AgentSuggestionResponse("/search", "Tool · Knowledge", AgentSuggestionKind.Tool),
        ],
            response.Suggestions);
    }

    [Fact]
    public async Task GetSuggestionsAsync_SystemAgent_IgnoresMalformedAndUnknownTools()
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "system-agent",
            Type = AgentType.System,
            Tools = "{malformed",
        };
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Tools = """["known","unknown","KNOWN"]""",
        };
        var service = CreateService(
            agents: [agent],
            projects: [project],
            tools: [new TestTool("known", "Default", "Known tool")]);

        var response = await service.GetSuggestionsAsync(project.Id, agent.Id);

        var suggestion = Assert.Single(response.Suggestions);
        Assert.Equal("/known", suggestion.Text);
        Assert.Equal(AgentSuggestionKind.Tool, suggestion.Kind);
    }

    [Fact]
    public async Task GetSuggestionsAsync_WithoutProject_UsesOnlyAgentCapabilities()
    {
        var agentSkill = new Skill
        {
            Id = Guid.NewGuid(),
            Name = "agent-skill",
        };
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "system-agent",
            Type = AgentType.System,
            Tools = """["agent-tool"]""",
            AgentSkillRelations =
            [
                new AgentSkillRelation { SkillId = agentSkill.Id },
            ],
        };
        var service = CreateService(
            agents: [agent],
            skills: [agentSkill],
            tools: [new TestTool("agent-tool", "Default", "Agent tool")]);

        var response = await service.GetSuggestionsAsync(default, agent.Id);

        Assert.Equal(AgentSuggestionMode.System, response.Mode);
        Assert.Equal(
            ["/agent-skill", "/agent-tool"],
            response.Suggestions.Select(item => item.Text).ToArray());
    }

    [Theory]
    [InlineData(AgentNames.ClaudeCode, AgentSuggestionMode.ClaudeCode)]
    [InlineData(AgentNames.Codex, AgentSuggestionMode.Unsupported)]
    [InlineData("OtherExternal", AgentSuggestionMode.Unsupported)]
    public async Task GetSuggestionsAsync_ExternalAgent_ReturnsExpectedMode(
        string agentName,
        AgentSuggestionMode expectedMode)
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = agentName,
            Type = AgentType.External,
        };
        var project = new Project { Id = Guid.NewGuid() };
        var service = CreateService(agents: [agent], projects: [project]);

        var response = await service.GetSuggestionsAsync(project.Id, agent.Id);

        Assert.Equal(expectedMode, response.Mode);
        Assert.Empty(response.Suggestions);
    }

    [Fact]
    public async Task GetSuggestionsAsync_MissingAgent_ThrowsAgentNotFound()
    {
        var service = CreateService(projects: [new Project { Id = Guid.NewGuid() }]);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.GetSuggestionsAsync(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(ErrorCodes.AgentNotFound.Code, exception.Code);
    }

    [Fact]
    public async Task GetSuggestionsAsync_MissingProject_ThrowsResourceNotFoundWithProjectContext()
    {
        var agent = new Agent { Id = Guid.NewGuid(), Name = "system-agent" };
        var projectId = Guid.NewGuid();
        var service = CreateService(agents: [agent]);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.GetSuggestionsAsync(projectId, agent.Id));

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        Assert.Contains(projectId.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuggestionsAsync_ReturnsApiResultAndSerializesEnumsAsContractStrings()
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "system-agent",
            Type = AgentType.System,
        };
        var project = new Project { Id = Guid.NewGuid() };
        var service = CreateService(agents: [agent], projects: [project]);
        var controller = new AgentsController(null!, service);

        var result = await controller.SuggestionsAsync(project.Id, agent.Id);
        var json = JsonSerializer.Serialize(
            new AgentSuggestionsResponse(
                AgentSuggestionMode.ClaudeCode,
                [new AgentSuggestionResponse("/deploy", "Skill", AgentSuggestionKind.Skill)]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("ApiResult", result.GetType().Name, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"claudeCode\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"skill\"", json, StringComparison.Ordinal);
        Assert.Contains(
            typeof(AgentsController)
                .GetMethod(nameof(AgentsController.SuggestionsAsync))!
                .GetCustomAttributes(typeof(ProducesApiResultAttribute), inherit: true),
            attribute => ((ProducesApiResultAttribute)attribute).Type.Name.StartsWith("ApiResult", StringComparison.Ordinal));
    }

    private static AgentSuggestionAppService CreateService(
        IEnumerable<Agent>? agents = null,
        IEnumerable<Project>? projects = null,
        IEnumerable<Skill>? skills = null,
        IEnumerable<IAgwTool>? tools = null)
    {
        var registry = new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            new ServiceCollection().BuildServiceProvider());
        foreach (var tool in tools ?? [])
        {
            registry.RegisterTool(tool);
        }

        return new AgentSuggestionAppService(
            new TestRepository<Agent>(agents),
            new TestRepository<Project>(projects),
            new TestRepository<Skill>(skills),
            registry);
    }

    private sealed class TestRepository<TEntity> : IRepository<TEntity> where TEntity : class
    {
        private readonly List<TEntity> _items;

        public TestRepository(IEnumerable<TEntity>? items = null)
        {
            _items = items?.ToList() ?? [];
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

        public Task AddAsync(TEntity entity) => Task.CompletedTask;

        public void Update(TEntity entity)
        {
        }

        public void Remove(TEntity entity)
        {
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestTool : IAgwTool
    {
        public TestTool(string name, string category, string description)
        {
            Name = name;
            Category = category;
            Description = description;
        }

        public string Name { get; }

        public string Category { get; }

        public string Description { get; }

        public AITool ToAITool() => AIFunctionFactory.Create(
            (Func<string>)(() => Name),
            new AIFunctionFactoryOptions { Name = Name, Description = Description });
    }
}
