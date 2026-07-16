using System.Linq.Expressions;
using System.Text.Json;

using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public class AgentAppServiceUpdateTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task UpdateAgentAsync_ExternalAllowedFields_UpdatesSpecifiedValuesAndPreservesRelations()
    {
        var newModelProviderId = Guid.CreateVersion7();
        var summaryModelProviderId = Guid.CreateVersion7();
        var agent = CreateExternalAgent();
        agent.EnableSummary = true;
        agent.SummaryModelProviderId = summaryModelProviderId;
        var mcpRelations = new TestRepository<AgentMcpServerRelation>(
            [new AgentMcpServerRelation { AgentId = agent.Id, McpToolServerId = Guid.CreateVersion7() }])
        {
            ThrowOnList = true
        };
        var skillRelations = new TestRepository<AgentSkillRelation>(
            [new AgentSkillRelation { AgentId = agent.Id, SkillId = Guid.CreateVersion7() }])
        {
            ThrowOnList = true
        };
        var connectionRelations = new TestRepository<AgentConnectionRelation>(
            [new AgentConnectionRelation { AgentId = agent.Id, ConnectionId = Guid.CreateVersion7() }])
        {
            ThrowOnList = true
        };
        var service = CreateService(
            agent,
            modelProviderIds: [newModelProviderId, summaryModelProviderId],
            mcpRelationRepository: mcpRelations,
            skillRelationRepository: skillRelations,
            connectionRelationRepository: connectionRelations);
        var request = Deserialize(
            $$"""
            {
              "displayName": "",
              "description": "",
              "modelProviderId": "{{newModelProviderId}}",
              "extra": "  {\"sandbox\":false}  ",
              "environmentVariables": { "  TOKEN  ": "value" }
            }
            """);

        var updated = await service.UpdateAgentAsync(agent.Id, request.ToCommand(), "tester");

        Assert.Same(agent, updated);
        Assert.Equal("", agent.DisplayName);
        Assert.Equal("", agent.Description);
        Assert.Equal(newModelProviderId, agent.ModelProviderId);
        Assert.Equal("{\"sandbox\":false}", agent.Extra);
        Assert.Equal("value", agent.EnvironmentVariables["TOKEN"]);
        Assert.Equal("original-prompt", agent.SystemPrompt);
        Assert.Equal("[\"original-tool\"]", agent.Tools);
        Assert.True(agent.EnableSummary);
        Assert.Equal(summaryModelProviderId, agent.SummaryModelProviderId);
        Assert.Equal(0, mcpRelations.ListCallCount);
        Assert.Equal(0, skillRelations.ListCallCount);
        Assert.Equal(0, connectionRelations.ListCallCount);
    }

    [Theory]
    [InlineData("displayName")]
    [InlineData("description")]
    [InlineData("modelProviderId")]
    [InlineData("extra")]
    [InlineData("environmentVariables")]
    public async Task UpdateAgentAsync_ExternalSingleAllowedField_UpdatesOnlySpecifiedField(
        string fieldName)
    {
        var originalModelProviderId = Guid.CreateVersion7();
        var updatedModelProviderId = Guid.CreateVersion7();
        var agent = CreateExternalAgent();
        agent.ModelProviderId = originalModelProviderId;
        var service = CreateService(
            agent,
            modelProviderIds: [originalModelProviderId, updatedModelProviderId]);
        var json = fieldName switch
        {
            "displayName" => "{\"displayName\":\"Updated Agent\"}",
            "description" => "{\"description\":\"Updated description\"}",
            "modelProviderId" => $"{{\"modelProviderId\":\"{updatedModelProviderId}\"}}",
            "extra" => "{\"extra\":\"{\\\"updated\\\":true}\"}",
            "environmentVariables" => "{\"environmentVariables\":{\"TOKEN\":\"updated\"}}",
            _ => throw new Xunit.Sdk.XunitException($"Unexpected field: {fieldName}")
        };

        var updated = await service.UpdateAgentAsync(
            agent.Id,
            Deserialize(json).ToCommand(),
            "tester");

        Assert.Same(agent, updated);
        Assert.Equal(
            fieldName == "displayName" ? "Updated Agent" : "External Agent",
            agent.DisplayName);
        Assert.Equal(
            fieldName == "description" ? "Updated description" : "Original description",
            agent.Description);
        Assert.Equal(
            fieldName == "modelProviderId" ? updatedModelProviderId : originalModelProviderId,
            agent.ModelProviderId);
        Assert.Equal(
            fieldName == "extra" ? "{\"updated\":true}" : "{\"original\":true}",
            agent.Extra);
        Assert.Equal(
            fieldName == "environmentVariables" ? "updated" : "original",
            agent.EnvironmentVariables["TOKEN"]);
    }

    [Fact]
    public async Task UpdateAgentAsync_ExternalOmittedFields_PreservesAllowedValues()
    {
        var agent = CreateExternalAgent();
        var service = CreateService(agent);

        var updated = await service.UpdateAgentAsync(
            agent.Id,
            Deserialize("{}").ToCommand(),
            "tester");

        Assert.Same(agent, updated);
        Assert.Equal("External Agent", agent.DisplayName);
        Assert.Equal("Original description", agent.Description);
        Assert.Null(agent.ModelProviderId);
        Assert.Equal("{\"original\":true}", agent.Extra);
        Assert.Equal("original", agent.EnvironmentVariables["TOKEN"]);
    }

    [Fact]
    public async Task UpdateAgentAsync_ExternalExplicitNulls_ClearNullableAllowedFields()
    {
        var modelProviderId = Guid.CreateVersion7();
        var agent = CreateExternalAgent();
        agent.ModelProviderId = modelProviderId;
        var service = CreateService(agent, modelProviderIds: [modelProviderId]);
        var request = Deserialize(
            """
            {
              "modelProviderId": null,
              "extra": null,
              "environmentVariables": null
            }
            """);

        var updated = await service.UpdateAgentAsync(agent.Id, request.ToCommand(), "tester");

        Assert.Same(agent, updated);
        Assert.Null(agent.ModelProviderId);
        Assert.Null(agent.Extra);
        Assert.Empty(agent.EnvironmentVariables);
    }

    [Theory]
    [InlineData("displayName")]
    [InlineData("description")]
    public async Task UpdateAgentAsync_ExternalNullRequiredString_ThrowsInvalidParam(string fieldName)
    {
        var agent = CreateExternalAgent();
        var service = CreateService(agent);
        var request = Deserialize($"{{\"{fieldName}\":null}}");

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.UpdateAgentAsync(agent.Id, request.ToCommand(), "tester"));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains(fieldName, exception.Message, StringComparison.Ordinal);
        Assert.Null(agent.UpdateBy);
    }

    [Theory]
    [InlineData("{\"systemPrompt\":null}", "systemPrompt")]
    [InlineData("{\"tools\":null}", "tools")]
    [InlineData("{\"skillIds\":[]}", "skillIds")]
    [InlineData("{\"mcpToolServerIds\":[]}", "mcpToolServerIds")]
    [InlineData("{\"connectionIds\":[]}", "connectionIds")]
    [InlineData("{\"enableSummary\":false}", "enableSummary")]
    [InlineData("{\"summaryModelProviderId\":null}", "summaryModelProviderId")]
    public async Task UpdateAgentAsync_ExternalForbiddenField_ThrowsBeforeMutation(
        string json,
        string fieldName)
    {
        var agent = CreateExternalAgent();
        var unitOfWork = new TestUnitOfWork();
        var service = CreateService(agent, unitOfWork: unitOfWork);
        var request = Deserialize(json);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.UpdateAgentAsync(agent.Id, request.ToCommand(), "tester"));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains(fieldName, exception.Message, StringComparison.Ordinal);
        Assert.Null(agent.UpdateBy);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task UpdateAgentAsync_SystemAgent_KeepsFullUpdateBehavior()
    {
        var oldModelProviderId = Guid.CreateVersion7();
        var newModelProviderId = Guid.CreateVersion7();
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Type = AgentType.System,
            DisplayName = "Before",
            Description = "Before",
            SystemPrompt = "Before",
            ModelProviderId = oldModelProviderId,
            EnableSummary = true,
            Tools = "[\"before\"]",
            EnvironmentVariables = new Dictionary<string, string> { ["BEFORE"] = "value" }
        };
        var service = CreateService(agent, modelProviderIds: [oldModelProviderId, newModelProviderId]);
        var request = Deserialize(
            $$"""
            {
              "displayName": "After",
              "description": "After",
              "systemPrompt": "After",
              "modelProviderId": "{{newModelProviderId}}"
            }
            """);

        var updated = await service.UpdateAgentAsync(agent.Id, request.ToCommand(), "tester");

        Assert.Same(agent, updated);
        Assert.Equal("After", agent.DisplayName);
        Assert.Equal("After", agent.Description);
        Assert.Equal("After", agent.SystemPrompt);
        Assert.Equal(newModelProviderId, agent.ModelProviderId);
        Assert.False(agent.EnableSummary);
        Assert.Null(agent.Tools);
        Assert.Empty(agent.EnvironmentVariables);
    }

    [Fact]
    public async Task UpdateAgentAsync_SystemAgentWithNullEnableSummary_ThrowsInvalidParam()
    {
        var modelProviderId = Guid.CreateVersion7();
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Type = AgentType.System,
            ModelProviderId = modelProviderId
        };
        var service = CreateService(agent, modelProviderIds: [modelProviderId]);
        var request = Deserialize(
            $$"""
            {
              "displayName": "Agent",
              "description": "Description",
              "systemPrompt": "Prompt",
              "modelProviderId": "{{modelProviderId}}",
              "enableSummary": null
            }
            """);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.UpdateAgentAsync(agent.Id, request.ToCommand(), "tester"));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains("enableSummary", exception.Message, StringComparison.Ordinal);
        Assert.Null(agent.UpdateBy);
    }

    [Fact]
    public async Task UpdateAgentAsync_SystemAgentWithoutModelProviderField_ThrowsInvalidParam()
    {
        var modelProviderId = Guid.CreateVersion7();
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Type = AgentType.System,
            ModelProviderId = modelProviderId
        };
        var service = CreateService(agent, modelProviderIds: [modelProviderId]);
        var request = Deserialize(
            """
            {
              "displayName": "Agent",
              "description": "Description",
              "systemPrompt": "Prompt"
            }
            """);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.UpdateAgentAsync(agent.Id, request.ToCommand(), "tester"));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains("modelProviderId", exception.Message, StringComparison.Ordinal);
        Assert.Null(agent.UpdateBy);
    }

    private static Agent CreateExternalAgent() => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "external-agent",
        Type = AgentType.External,
        DisplayName = "External Agent",
        Description = "Original description",
        SystemPrompt = "original-prompt",
        Tools = "[\"original-tool\"]",
        Extra = "{\"original\":true}",
        EnvironmentVariables = new Dictionary<string, string> { ["TOKEN"] = "original" }
    };

    private static AgentUpdateRequest Deserialize(string json) =>
        JsonSerializer.Deserialize<AgentUpdateRequest>(json, JsonOptions)
        ?? throw new Xunit.Sdk.XunitException("Agent update request did not deserialize.");

    private static AgentAppService CreateService(
        Agent agent,
        IEnumerable<Guid>? modelProviderIds = null,
        TestRepository<AgentMcpServerRelation>? mcpRelationRepository = null,
        TestRepository<AgentSkillRelation>? skillRelationRepository = null,
        TestRepository<AgentConnectionRelation>? connectionRelationRepository = null,
        TestUnitOfWork? unitOfWork = null)
    {
        var modelProviders = (modelProviderIds ?? [])
            .Select(id => new ModelProviderRelation { Id = id })
            .ToArray();
        return new AgentAppService(
            new TestRepository<Agent>([agent], item => item.Id),
            connectionRelationRepository ?? new TestRepository<AgentConnectionRelation>(),
            new TestRepository<Connection>(),
            new TestRepository<ModelProviderRelation>(modelProviders, item => item.Id),
            new TestRepository<LlmModel>(),
            new TestRepository<Provider>(),
            new TestRepository<McpServer>(),
            mcpRelationRepository ?? new TestRepository<AgentMcpServerRelation>(),
            new TestRepository<Skill>(),
            skillRelationRepository ?? new TestRepository<AgentSkillRelation>(),
            unitOfWork ?? new TestUnitOfWork(),
            new AgentDomainService(TimeProvider.System));
    }

    private sealed class TestRepository<TEntity> : IRepository<TEntity> where TEntity : class
    {
        private readonly Func<TEntity, object?>? _idSelector;
        private readonly List<TEntity> _items;

        public TestRepository(
            IEnumerable<TEntity>? items = null,
            Func<TEntity, object?>? idSelector = null)
        {
            _items = items?.ToList() ?? [];
            _idSelector = idSelector;
        }

        public IQueryable<TEntity> Queryable => _items.AsQueryable();
        public bool ThrowOnList { get; init; }
        public int ListCallCount { get; private set; }

        public Task<TEntity?> GetByIdAsync(object id) => Task.FromResult(
            _idSelector == null
                ? null
                : _items.SingleOrDefault(item => Equals(_idSelector(item), id)));

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
                throw new Xunit.Sdk.XunitException($"Unexpected {typeof(TEntity).Name} relation sync.");
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
        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync()
        {
            SaveCount++;
            return Task.FromResult(0);
        }
    }
}
