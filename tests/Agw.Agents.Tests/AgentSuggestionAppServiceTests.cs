using System.Linq.Expressions;
using System.Text.Json;

using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Definitions.Controllers;
using Agw.Agents.ExternalAgents;
using Agw.Domain.Services;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Entities.Tools;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Agw.Tools.ContextualTools.WebSearch;
using Agw.Tools.ToolBlocks;
using Agw.Tools.ToolBlocks.Blocks.Mode;
using Agw.Tools.ToolBlocks.Blocks.Todo;

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
            Id = Guid.CreateVersion7(),
            Name = "deploy",
            Description = "Deploy the application",
        };
        var reviewSkill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "review",
        };
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            Type = AgentType.System,
            Tools =
            [
                new ToolValue { Definition = new BashToolDefinition() },
                new ToolBlockValue { Definition = new TodoToolBlockDefinition() }
            ],
            AgentSkillRelations =
            [
                new AgentSkillRelation { SkillId = deploySkill.Id },
                new AgentSkillRelation { SkillId = reviewSkill.Id },
            ],
        };
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Tools =
            [
                new ToolValue { Definition = new WebFetchToolDefinition() },
                new ToolBlockValue { Definition = new ModeToolBlockDefinition() }
            ],
            ProjectSkillRelations =
            [
                new ProjectSkillRelation { SkillId = deploySkill.Id },
            ],
        };
        var service = CreateService(
            agents: [agent],
            projects: [project],
            skills: [deploySkill, reviewSkill]);

        var response = await service.GetSuggestionsAsync(project.Id, agent.Id);

        Assert.Equal(AgentSuggestionMode.System, response.Mode);
        Assert.Equal(
            [
                "/bash",
                "/deploy",
                "/mode_get",
                "/mode_set",
                "/review",
                "/todos_add",
                "/todos_complete",
                "/todos_get_all",
                "/todos_get_remaining",
                "/todos_remove",
                "/web_fetch"
            ],
            response.Suggestions.Select(static suggestion => suggestion.Text));
        Assert.Contains(
            response.Suggestions,
            static suggestion => suggestion.Text == "/bash" && suggestion.Kind == AgentSuggestionKind.Tool);
        Assert.Contains(
            response.Suggestions,
            static suggestion => suggestion.Text == "/deploy" && suggestion.Kind == AgentSuggestionKind.Skill);
        Assert.Contains(
            response.Suggestions,
            static suggestion =>
                suggestion.Text == "/mode_get" &&
                suggestion.Description == "Tool · Mode · Allows the agent to switch between plan and execute modes." &&
                suggestion.Kind == AgentSuggestionKind.Tool);
        Assert.Contains(
            response.Suggestions,
            static suggestion =>
                suggestion.Text == "/todos_add" &&
                suggestion.Description == "Tool · Todo · Tracks multi-step work with a persistent todo list." &&
                suggestion.Kind == AgentSuggestionKind.Tool);
    }

    [Fact]
    public async Task GetSuggestionsAsync_SystemAgent_DeduplicatesAgentAndProjectTools()
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            Type = AgentType.System,
            Tools =
            [
                new ToolValue { Definition = new WebFetchToolDefinition() }
            ],
        };
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Tools =
            [
                new ToolValue { Definition = new WebFetchToolDefinition() }
            ],
        };
        var service = CreateService(
            agents: [agent],
            projects: [project]);

        var response = await service.GetSuggestionsAsync(project.Id, agent.Id);

        var suggestion = Assert.Single(response.Suggestions);
        Assert.Equal("/web_fetch", suggestion.Text);
        Assert.Equal(AgentSuggestionKind.Tool, suggestion.Kind);
    }

    [Fact]
    public async Task GetSuggestionsAsync_SystemAgent_DeduplicatesAgentAndProjectToolBlockMembers()
    {
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            Type = AgentType.System,
            Tools =
            [
                new ToolBlockValue { Definition = new TodoToolBlockDefinition() }
            ],
        };
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Tools =
            [
                new ToolBlockValue { Definition = new TodoToolBlockDefinition() }
            ],
        };
        var service = CreateService(
            agents: [agent],
            projects: [project]);

        var response = await service.GetSuggestionsAsync(project.Id, agent.Id);

        Assert.Equal(
            ["/todos_add", "/todos_complete", "/todos_get_all", "/todos_get_remaining", "/todos_remove"],
            response.Suggestions.Select(static suggestion => suggestion.Text));
        Assert.All(
            response.Suggestions,
            static suggestion =>
            {
                Assert.Equal("Tool · Todo · Tracks multi-step work with a persistent todo list.", suggestion.Description);
                Assert.Equal(AgentSuggestionKind.Tool, suggestion.Kind);
            });
    }

    [Fact]
    public async Task GetSuggestionsAsync_WithoutProject_UsesOnlyAgentCapabilities()
    {
        var agentSkill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "agent-skill",
        };
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            Type = AgentType.System,
            Tools =
            [
                new ToolValue { Definition = new WebSearchToolDefinition() }
            ],
            AgentSkillRelations =
            [
                new AgentSkillRelation { SkillId = agentSkill.Id },
            ],
        };
        var service = CreateService(
            agents: [agent],
            skills: [agentSkill]);

        var response = await service.GetSuggestionsAsync(default, agent.Id);

        Assert.Equal(AgentSuggestionMode.System, response.Mode);
        Assert.Equal(
            ["/agent-skill", "/web_search"],
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
            Id = Guid.CreateVersion7(),
            Name = agentName,
            Type = AgentType.External,
        };
        var project = new Project { Id = Guid.CreateVersion7() };
        var service = CreateService(agents: [agent], projects: [project]);

        var response = await service.GetSuggestionsAsync(project.Id, agent.Id);

        Assert.Equal(expectedMode, response.Mode);
        Assert.Empty(response.Suggestions);
    }

    [Fact]
    public async Task GetSuggestionsAsync_MissingAgent_ThrowsAgentNotFound()
    {
        var service = CreateService(projects: [new Project { Id = Guid.CreateVersion7() }]);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.GetSuggestionsAsync(Guid.CreateVersion7(), Guid.CreateVersion7()));

        Assert.Equal(ErrorCodes.AgentNotFound.Code, exception.Code);
    }

    [Fact]
    public async Task GetSuggestionsAsync_MissingProject_ThrowsResourceNotFoundWithProjectContext()
    {
        var agent = new Agent { Id = Guid.CreateVersion7(), Name = "system-agent" };
        var projectId = Guid.CreateVersion7();
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
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            Type = AgentType.System,
        };
        var project = new Project { Id = Guid.CreateVersion7() };
        var service = CreateService(agents: [agent], projects: [project]);
        var controller = new AgentsController(null!, service);

        var result = await controller.SuggestionsAsync(project.Id, agent.Id);
        var json = JsonSerializer.Serialize(
            new AgentSuggestionsResponse(
                AgentSuggestionMode.ClaudeCode,
                [
                    new AgentSuggestionResponse("/deploy", "Skill", AgentSuggestionKind.Skill),
                    new AgentSuggestionResponse("/todos_add", "Tool · Todo", AgentSuggestionKind.Tool)
                ]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("ApiResult", result.GetType().Name, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"claudeCode\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"skill\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"tool\"", json, StringComparison.Ordinal);
        Assert.Contains(
            typeof(AgentsController)
                .GetMethod(nameof(AgentsController.SuggestionsAsync))!
                .GetCustomAttributes(typeof(ProducesApiResultAttribute), inherit: true),
            attribute => ((ProducesApiResultAttribute)attribute).Type.Name.StartsWith("ApiResult", StringComparison.Ordinal));
    }

    private static AgentSuggestionAppService CreateService(
        IEnumerable<Agent>? agents = null,
        IEnumerable<Project>? projects = null,
        IEnumerable<Skill>? skills = null)
    {
        var registry = new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            new ServiceCollection().BuildServiceProvider(),
            [new WebSearchContextualTool()],
            new ToolBlockRegistry([new TodoToolBlock(), new ModeToolBlock()]));
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

}
