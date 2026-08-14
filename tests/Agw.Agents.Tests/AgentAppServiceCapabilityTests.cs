using System.Linq.Expressions;

using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;

namespace Agw.Agents.Tests;

public class AgentAppServiceCapabilityTests
{
    [Fact]
    public void CreateAiAgentRequest_DefaultMode_IsExecute()
    {
        var request = new CreateAiAgentRequest
        {
            Agent = new Agent()
        };

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
        var agentSkillId = Guid.CreateVersion7();
        var projectSkillId = Guid.CreateVersion7();
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
    public async Task ListEnabledMcpToolServersByAgentAsync_AgentCompatibilityWrapper_UsesAgentRelations()
    {
        var agentId = Guid.CreateVersion7();
        var server = new McpServer { Id = Guid.CreateVersion7(), Name = "agent-mcp", Enabled = true };
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
        var agentId = Guid.CreateVersion7();
        var skill = new Skill { Id = Guid.CreateVersion7(), Name = "agent-skill" };
        var service = CreateService(
            skills: [skill],
            agentSkillRelations:
            [
                new AgentSkillRelation { AgentId = agentId, SkillId = skill.Id },
            ]);

        var skills = await service.ListSkillsByAgentAsync(agentId);

        Assert.Equal(skill.Id, Assert.Single(skills).Id);
    }

    private static AgentAppService CreateService(
        IEnumerable<McpServer>? mcpServers = null,
        IEnumerable<Skill>? skills = null,
        IEnumerable<AgentMcpServerRelation>? agentMcpRelations = null,
        IEnumerable<AgentSkillRelation>? agentSkillRelations = null)
    {
        return new AgentAppService(
            new TestRepository<Agent>(),
            new TestRepository<AgentConnectionRelation>(),
            new TestRepository<Connection>(),
            new TestRepository<ModelProviderRelation>(),
            new TestRepository<AgwAiModel>(),
            new TestRepository<Provider>(),
            new TestRepository<McpServer>(mcpServers),
            new TestRepository<AgentMcpServerRelation>(agentMcpRelations),
            new TestRepository<Skill>(skills),
            new TestRepository<AgentSkillRelation>(agentSkillRelations),
            new TestUnitOfWork(),
            new AgentDomainService(TimeProvider.System));
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
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<bool> SaveEntitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public void Dispose()
        {
        }
    }

}
