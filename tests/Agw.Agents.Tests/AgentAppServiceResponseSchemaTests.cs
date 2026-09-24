using System.Linq.Expressions;
using System.Text.Json;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Definitions.Controllers;
using Agw.Providers.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public class AgentAppServiceResponseSchemaTests : IDisposable
{
    private const string Schema =
        """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""";

    private readonly TestAgentDatabase _database = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task CreateAgentAsync_SystemAgent_TrimsAndSavesSchema()
    {
        var modelProviderId = Guid.CreateVersion7();
        var service = CreateService(modelProviderIds: [modelProviderId]);
        var agent = CreateSystemAgent(modelProviderId);
        agent.ResponseSchema = $"  {Schema}  ";

        var created = await service.CreateAgentAsync(agent, null, null, null);

        Assert.NotNull(created);
        Assert.Equal(Schema, created.ResponseSchema);
    }

    [Fact]
    public async Task CreateAgentAsync_BlankSchema_SavesNull()
    {
        var modelProviderId = Guid.CreateVersion7();
        var service = CreateService(modelProviderIds: [modelProviderId]);
        var agent = CreateSystemAgent(modelProviderId);
        agent.ResponseSchema = "   ";

        var created = await service.CreateAgentAsync(agent, null, null, null);

        Assert.NotNull(created);
        Assert.Null(created.ResponseSchema);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("42")]
    public async Task CreateAgentAsync_InvalidSchema_ThrowsSharedError(string schema)
    {
        var modelProviderId = Guid.CreateVersion7();
        var service = CreateService(modelProviderIds: [modelProviderId]);
        var agent = CreateSystemAgent(modelProviderId);
        agent.ResponseSchema = schema;

        Assert.Equal(
            ErrorCodes.InvalidParam.Code,
            (await Assert.ThrowsAsync<AgwException>(() => service.CreateAgentAsync(agent, null, null, null))).Code
        );
    }

    [Fact]
    public async Task CreateAgentAsync_ExternalAgent_SavesSchema()
    {
        var service = CreateService();
        var agent = CreateExternalAgent();
        agent.ResponseSchema = Schema;

        var created = await service.CreateAgentAsync(agent, null, null, null);

        Assert.NotNull(created);
        Assert.Equal(Schema, created.ResponseSchema);
    }

    [Fact]
    public async Task UpdateAgentAsync_SystemAgent_SpecifiedNull_ClearsSchema()
    {
        var modelProviderId = Guid.CreateVersion7();
        var agent = CreateSystemAgent(modelProviderId);
        agent.ResponseSchema = Schema;
        var service = CreateService(agent, [modelProviderId]);
        var request = Deserialize(
            $$"""
            {
              "displayName": "System Agent",
              "description": "",
              "systemPrompt": "prompt",
              "modelProviderId": "{{modelProviderId}}",
              "responseSchema": null
            }
            """
        );

        var updated = await service.UpdateAgentAsync(agent.Id, request.ToCommand());

        Assert.NotNull(updated);
        Assert.Null(agent.ResponseSchema);
    }

    [Fact]
    public async Task UpdateAgentAsync_SystemAgent_MissingField_PreservesSchema()
    {
        var modelProviderId = Guid.CreateVersion7();
        var agent = CreateSystemAgent(modelProviderId);
        agent.ResponseSchema = Schema;
        var service = CreateService(agent, [modelProviderId]);
        var request = Deserialize(
            $$"""
            {
              "displayName": "System Agent",
              "description": "",
              "systemPrompt": "prompt",
              "modelProviderId": "{{modelProviderId}}"
            }
            """
        );

        var updated = await service.UpdateAgentAsync(agent.Id, request.ToCommand());

        Assert.NotNull(updated);
        Assert.Equal(Schema, agent.ResponseSchema);
    }

    [Fact]
    public async Task UpdateAgentAsync_SystemAgent_ReplacesAndTrimsSchema()
    {
        var modelProviderId = Guid.CreateVersion7();
        var agent = CreateSystemAgent(modelProviderId);
        var service = CreateService(agent, [modelProviderId]);
        var request = Deserialize(
            $$"""
            {
              "displayName": "System Agent",
              "description": "",
              "systemPrompt": "prompt",
              "modelProviderId": "{{modelProviderId}}",
              "responseSchema": "  {\"type\":\"object\"}  "
            }
            """
        );

        var updated = await service.UpdateAgentAsync(agent.Id, request.ToCommand());

        Assert.NotNull(updated);
        Assert.Equal("{\"type\":\"object\"}", agent.ResponseSchema);
    }

    [Fact]
    public async Task UpdateAgentAsync_ExternalAgent_UpdatesSchema()
    {
        var agent = CreateExternalAgent();
        var service = CreateService(agent);
        var request = Deserialize(
            $$"""
            {
              "responseSchema": "{{Schema.Replace("\"", "\\\"")}}"
            }
            """
        );

        var updated = await service.UpdateAgentAsync(agent.Id, request.ToCommand());

        Assert.NotNull(updated);
        Assert.Equal(Schema, agent.ResponseSchema);
    }

    [Fact]
    public async Task CreateAgentAsync_PiAgent_WithSchema_ThrowsSharedError()
    {
        var service = CreateService();
        var agent = CreateExternalAgent();
        agent.ExternalAgentKind = EngineKind.Pi;
        agent.ResponseSchema = Schema;

        Assert.Equal(
            ErrorCodes.InvalidParam.Code,
            (await Assert.ThrowsAsync<AgwException>(() => service.CreateAgentAsync(agent, null, null, null))).Code
        );
    }

    [Fact]
    public async Task UpdateAgentAsync_PiAgent_WithSchema_ThrowsSharedError()
    {
        var agent = CreateExternalAgent();
        agent.ExternalAgentKind = EngineKind.Pi;
        var service = CreateService(agent);
        var request = Deserialize("""{"responseSchema": "{\"type\":\"object\"}"}""");

        Assert.Equal(
            ErrorCodes.InvalidParam.Code,
            (await Assert.ThrowsAsync<AgwException>(() => service.UpdateAgentAsync(agent.Id, request.ToCommand()))).Code
        );
    }

    [Fact]
    public async Task UpdateAgentAsync_PiAgent_NullSchema_IsAllowed()
    {
        var agent = CreateExternalAgent();
        agent.ExternalAgentKind = EngineKind.Pi;
        var service = CreateService(agent);
        var request = Deserialize("""{"responseSchema": null}""");

        var updated = await service.UpdateAgentAsync(agent.Id, request.ToCommand());

        Assert.NotNull(updated);
        Assert.Null(agent.ResponseSchema);
    }

    [Fact]
    public async Task CreateAsync_Controller_MapsResponseSchemaIntoResult()
    {
        var modelProviderId = Guid.CreateVersion7();
        var service = CreateService(modelProviderIds: [modelProviderId]);
        var controller = new AgentsController(service, null!);

        var result = await controller.CreateAsync(
            new AgentCreateRequest(
                "System Agent",
                "system-agent",
                "",
                "prompt",
                modelProviderId,
                ResponseSchema: $"  {Schema}  "
            )
        );

        Assert.Contains("ApiResult", result.GetType().Name, StringComparison.Ordinal);
        // Audit interceptors do not run in this context, so CreateBy stays null and the owner
        // filter hides the row; bypass it to verify the persisted schema.
        var persisted = Assert.Single(_database.Context.Agents.IgnoreQueryFilters());
        Assert.Equal(Schema, persisted.ResponseSchema);
    }

    private static Agent CreateSystemAgent(Guid modelProviderId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = "system-agent",
            DisplayName = "System Agent",
            Type = AgentType.System,
            ModelProviderId = modelProviderId,
            CreateBy = "tester",
        };

    private static Agent CreateExternalAgent() =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = "external-agent",
            DisplayName = "External Agent",
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.ClaudeCode,
            CreateBy = "tester",
        };

    private static AgentUpdateRequest Deserialize(string json) =>
        JsonSerializer.Deserialize<AgentUpdateRequest>(json, JsonOptions)
        ?? throw new Xunit.Sdk.XunitException("Agent update request did not deserialize.");

    private AgentAppService CreateService(Agent? agent = null, IEnumerable<Guid>? modelProviderIds = null)
    {
        var model = new AgwAiModel
        {
            Id = Guid.CreateVersion7(),
            Name = "test-model",
            CreateBy = "tester",
        };
        var provider = new Provider
        {
            Id = Guid.CreateVersion7(),
            ProviderType = ProviderType.Anthropic,
            CreateBy = "tester",
        };
        var modelProviders = (modelProviderIds ?? [])
            .Select(id => new ModelProviderRelation
            {
                Id = id,
                ModelId = model.Id,
                ProviderId = provider.Id,
                CreateBy = "tester",
            })
            .ToArray();
        var userInfo = new TestUserInfoService();
        if (agent != null)
        {
            agent.CreateBy ??= "tester";
            _database.Context.Agents.Add(agent);
            _database.Context.SaveChanges();
        }

        return new AgentAppService(
            _database.Context,
            new TestConnectionReferenceFacade(new TestRepository<Connection>(), userInfo),
            new TestModelProviderReferenceFacade(
                new TestRepository<ModelProviderRelation>(modelProviders, item => item.Id),
                new TestRepository<AgwAiModel>([model]),
                new TestRepository<Provider>([provider]),
                userInfo
            ),
            new TestSkillReferenceFacade(new TestRepository<Skill>(), userInfo),
            userInfo,
            new Agw.Infrastructure.Agents.AgentDeletionCoordinator(
                _database.Context,
                Agw.Shared.Coordination.InMemoryApplicationLock.Shared
            )
        );
    }

    private sealed class TestRepository<TEntity> : IRepository<TEntity>
        where TEntity : class
    {
        private readonly Func<TEntity, object?>? _idSelector;
        private readonly List<TEntity> _items;

        public TestRepository(IEnumerable<TEntity>? items = null, Func<TEntity, object?>? idSelector = null)
        {
            _items = items?.ToList() ?? [];
            _idSelector = idSelector;
        }

        public IQueryable<TEntity> Queryable => _items.AsQueryable();

        public Task<TEntity?> GetByIdAsync(object id) =>
            Task.FromResult(_idSelector == null ? null : _items.SingleOrDefault(item => Equals(_idSelector(item), id)));

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
