using System.Linq.Expressions;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;

namespace Agw.Agents.Tests;

public class AgentAppServiceCapabilityTests : IDisposable
{
    private readonly TestAgentDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void CreateAiAgentRequest_DefaultMode_IsExecute()
    {
        var request = new CreateAiAgentRequest { Agent = new Agent() };

        Assert.Equal("execute", request.DefaultMode);
    }

    [Fact]
    public async Task ListEnabledMcpToolServersAsync_AgentAndProjectIds_DeduplicatesAndFiltersDisabledServers()
    {
        var agentServerId = Guid.CreateVersion7();
        var projectServerId = Guid.CreateVersion7();
        var disabledServerId = Guid.CreateVersion7();
        var service = CreateService(
            mcpServers:
            [
                new McpServer
                {
                    Id = agentServerId,
                    Name = "agent",
                    Enabled = true,
                },
                new McpServer
                {
                    Id = projectServerId,
                    Name = "project",
                    Enabled = true,
                },
                new McpServer
                {
                    Id = disabledServerId,
                    Name = "disabled",
                    Enabled = false,
                },
            ]
        );

        var servers = await service.ListEnabledMcpToolServersAsync(
            new[] { agentServerId, projectServerId, agentServerId, disabledServerId }
        );

        Assert.Equal(
            new[] { agentServerId, projectServerId }.OrderBy(id => id).ToArray(),
            servers.Select(server => server.Id).OrderBy(id => id).ToArray()
        );
    }

    [Fact]
    public async Task ListSkillsAsync_AgentAndProjectIds_DeduplicatesSkills()
    {
        var agentSkillId = Guid.CreateVersion7();
        var projectSkillId = Guid.CreateVersion7();
        var service = CreateService(
            skills:
            [
                new Skill { Id = agentSkillId, Name = "agent" },
                new Skill { Id = projectSkillId, Name = "project" },
            ]
        );

        var skills = await service.ListSkillsAsync(new[] { agentSkillId, projectSkillId, agentSkillId });

        Assert.Equal(
            new[] { agentSkillId, projectSkillId }.OrderBy(id => id).ToArray(),
            skills.Select(skill => skill.Id).OrderBy(id => id).ToArray()
        );
    }

    [Fact]
    public async Task ListEnabledMcpToolServersByAgentAsync_AgentCompatibilityWrapper_UsesAgentRelations()
    {
        var agentId = Guid.CreateVersion7();
        var server = new McpServer
        {
            Id = Guid.CreateVersion7(),
            Name = "agent-mcp",
            Enabled = true,
        };
        var service = CreateService(
            mcpServers: [server],
            agentMcpRelations: [new AgentMcpServerRelation { AgentId = agentId, McpToolServerId = server.Id }]
        );

        var servers = await service.ListEnabledMcpToolServersByAgentAsync(agentId);

        Assert.Equal(server.Id, Assert.Single(servers).Id);
    }

    [Fact]
    public async Task ListSkillsByAgentAsync_AgentCompatibilityWrapper_UsesAgentRelations()
    {
        var agentId = Guid.CreateVersion7();
        var skill = new Skill { Id = Guid.CreateVersion7(), Name = "agent-skill" };
        var service = CreateService(
            skills: [skill],
            agentSkillRelations: [new AgentSkillRelation { AgentId = agentId, SkillId = skill.Id }]
        );

        var skills = await service.ListSkillsByAgentAsync(agentId);

        Assert.Equal(skill.Id, Assert.Single(skills).Id);
    }

    private AgentAppService CreateService(
        IEnumerable<McpServer>? mcpServers = null,
        IEnumerable<Skill>? skills = null,
        IEnumerable<AgentMcpServerRelation>? agentMcpRelations = null,
        IEnumerable<AgentSkillRelation>? agentSkillRelations = null
    )
    {
        var serverItems = (mcpServers ?? []).ToArray();
        foreach (var server in serverItems)
        {
            server.CreateBy ??= "tester";
        }
        var skillItems = (skills ?? []).ToArray();
        foreach (var skill in skillItems)
        {
            skill.CreateBy ??= "tester";
        }
        var mcpRelations = (agentMcpRelations ?? []).ToArray();
        var skillRelations = (agentSkillRelations ?? []).ToArray();
        var agents = mcpRelations
            .Select(relation => relation.AgentId)
            .Concat(skillRelations.Select(relation => relation.AgentId))
            .Distinct()
            .Select(id => new Agent { Id = id, CreateBy = "tester" })
            .ToArray();
        var connectionRepository = new TestRepository<Connection>();
        var modelProviderRepository = new TestRepository<ModelProviderRelation>();
        var modelRepository = new TestRepository<AgwAiModel>();
        var providerRepository = new TestRepository<Provider>();
        var skillRepository = new TestRepository<Skill>(skillItems);
        var userInfo = new TestUserInfoService();

        _database.Context.Agents.AddRange(agents);
        _database.Context.McpToolServers.AddRange(serverItems);
        _database.Context.Skills.AddRange(skillItems);
        _database.Context.AgentMcpToolServers.AddRange(mcpRelations);
        _database.Context.AgentSkillRelations.AddRange(skillRelations);
        _database.Context.SaveChanges();

        return new AgentAppService(
            _database.Context,
            new TestConnectionReferenceFacade(connectionRepository, userInfo),
            new TestModelProviderReferenceFacade(
                modelProviderRepository,
                modelRepository,
                providerRepository,
                userInfo
            ),
            new TestSkillReferenceFacade(skillRepository, userInfo),
            userInfo
        );
    }

    private sealed class TestRepository<TEntity> : IRepository<TEntity>
        where TEntity : class
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
            CancellationToken cancellationToken = default
        ) => Task.FromResult(_items.AsQueryable().SingleOrDefault(predicate));

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null
        )
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
            params Expression<Func<TEntity, object>>[] includes
        ) => ListAsync(predicate, orderBy);

        public Task AddAsync(TEntity entity)
        {
            _items.Add(entity);
            return Task.CompletedTask;
        }

        public void Update(TEntity entity) { }

        public void Remove(TEntity entity) => _items.Remove(entity);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
