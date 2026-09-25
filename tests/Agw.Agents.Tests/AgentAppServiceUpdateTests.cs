using System.Linq.Expressions;
using System.Text.Json;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Providers.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Shared.Tooling;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public class AgentAppServiceUpdateTests : IDisposable
{
    private readonly TestAgentDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(EngineKind.ClaudeCode)]
    [InlineData(EngineKind.Codex)]
    [InlineData(EngineKind.Pi)]
    public async Task CreateAgentAsync_ExternalSummary_DoesNotPersistAgent(EngineKind kind)
    {
        // Arrange
        var summaryModelProviderId = Guid.CreateVersion7();
        var service = CreateService(CreateExternalAgent(), [summaryModelProviderId]);
        var agent = CreateExternalAgent();
        agent.ExternalAgentKind = kind;
        agent.Name = "new-agent";
        agent.EnableSummary = true;
        agent.SummaryModelProviderId = summaryModelProviderId;

        // Act
        var error = await Assert.ThrowsAsync<AgwException>(() => service.CreateAgentAsync(agent, null, null, null));

        // Assert
        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.False(
            await _database.Context.Agents.AnyAsync(item => item.Id == agent.Id, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task UpdateAgentAsync_ExternalAllowedFields_ClearsLegacySummaryAndPreservesRelations()
    {
        var newModelProviderId = Guid.CreateVersion7();
        var summaryModelProviderId = Guid.CreateVersion7();
        var agent = CreateExternalAgent();
        agent.EnableSummary = true;
        agent.SummaryModelProviderId = summaryModelProviderId;
        AgentMcpServerRelation[] mcpRelations =
        [
            new AgentMcpServerRelation { AgentId = agent.Id, McpToolServerId = Guid.CreateVersion7() },
        ];
        AgentSkillRelation[] skillRelations =
        [
            new AgentSkillRelation { AgentId = agent.Id, SkillId = Guid.CreateVersion7() },
        ];
        AgentConnectionRelation[] connectionRelations =
        [
            new AgentConnectionRelation { AgentId = agent.Id, ConnectionId = Guid.CreateVersion7() },
        ];
        var service = CreateService(
            agent,
            modelProviderIds: [newModelProviderId, summaryModelProviderId],
            mcpRelations: mcpRelations,
            skillRelations: skillRelations,
            connectionRelations: connectionRelations
        );
        var request = Deserialize(
            $$"""
            {
              "displayName": "",
              "description": "",
              "modelProviderId": "{{newModelProviderId}}",
              "extra": "  {\"sandbox\":false}  ",
              "environmentVariables": { "  TOKEN  ": "value" }
            }
            """
        );

        var updated = await service.UpdateAgentAsync(agent.Id, request.ToCommand());

        Assert.Same(agent, updated);
        Assert.Equal("", agent.DisplayName);
        Assert.Equal("", agent.Description);
        Assert.Equal(newModelProviderId, agent.ModelProviderId);
        Assert.Equal("{\"sandbox\":false}", agent.Extra);
        Assert.Equal("value", agent.EnvironmentVariables["TOKEN"]);
        Assert.Equal("original-prompt", agent.SystemPrompt);
        Assert.IsType<WebFetchToolDefinition>(Assert.IsType<ToolValue>(Assert.Single(agent.Tools)).Definition);
        Assert.False(agent.EnableSummary);
        Assert.Null(agent.SummaryModelProviderId);
        Assert.Equal(mcpRelations, _database.Context.AgentMcpToolServers.ToArray());
        Assert.Equal(skillRelations, _database.Context.AgentSkillRelations.ToArray());
        Assert.Equal(connectionRelations, _database.Context.AgentConnectionRelations.ToArray());
    }

    [Theory]
    [InlineData("displayName")]
    [InlineData("description")]
    [InlineData("modelProviderId")]
    [InlineData("extra")]
    [InlineData("environmentVariables")]
    public async Task UpdateAgentAsync_ExternalSingleAllowedField_UpdatesOnlySpecifiedField(string fieldName)
    {
        var originalModelProviderId = Guid.CreateVersion7();
        var updatedModelProviderId = Guid.CreateVersion7();
        var agent = CreateExternalAgent();
        agent.ModelProviderId = originalModelProviderId;
        var service = CreateService(agent, modelProviderIds: [originalModelProviderId, updatedModelProviderId]);
        var json = fieldName switch
        {
            "displayName" => "{\"displayName\":\"Updated Agent\"}",
            "description" => "{\"description\":\"Updated description\"}",
            "modelProviderId" => $"{{\"modelProviderId\":\"{updatedModelProviderId}\"}}",
            "extra" => "{\"extra\":\"{\\\"updated\\\":true}\"}",
            "environmentVariables" => "{\"environmentVariables\":{\"TOKEN\":\"updated\"}}",
            _ => throw new Xunit.Sdk.XunitException($"Unexpected field: {fieldName}"),
        };

        var updated = await service.UpdateAgentAsync(agent.Id, Deserialize(json).ToCommand());

        Assert.Same(agent, updated);
        Assert.Equal(fieldName == "displayName" ? "Updated Agent" : "External Agent", agent.DisplayName);
        Assert.Equal(fieldName == "description" ? "Updated description" : "Original description", agent.Description);
        Assert.Equal(
            fieldName == "modelProviderId" ? updatedModelProviderId : originalModelProviderId,
            agent.ModelProviderId
        );
        Assert.Equal(fieldName == "extra" ? "{\"updated\":true}" : "{\"original\":true}", agent.Extra);
        Assert.Equal(fieldName == "environmentVariables" ? "updated" : "original", agent.EnvironmentVariables["TOKEN"]);
    }

    [Fact]
    public async Task UpdateAgentAsync_ExternalOmittedFields_PreservesAllowedValues()
    {
        var agent = CreateExternalAgent();
        var service = CreateService(agent);

        var updated = await service.UpdateAgentAsync(agent.Id, Deserialize("{}").ToCommand());

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
            """
        );

        var updated = await service.UpdateAgentAsync(agent.Id, request.ToCommand());

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
            service.UpdateAgentAsync(agent.Id, request.ToCommand())
        );

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
    public async Task UpdateAgentAsync_ExternalForbiddenField_ThrowsBeforeMutation(string json, string fieldName)
    {
        var agent = CreateExternalAgent();
        var service = CreateService(agent);
        var request = Deserialize(json);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.UpdateAgentAsync(agent.Id, request.ToCommand())
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains(fieldName, exception.Message, StringComparison.Ordinal);
        Assert.Null(agent.UpdateBy);
        Assert.Equal("External Agent", _database.Context.Agents.AsNoTracking().Single().DisplayName);
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
            Tools = [new ToolValue { Definition = new WebSearchToolDefinition() }],
            EnvironmentVariables = new Dictionary<string, string> { ["BEFORE"] = "value" },
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
            """
        );

        var updated = await service.UpdateAgentAsync(agent.Id, request.ToCommand());

        Assert.Same(agent, updated);
        Assert.Equal("After", agent.DisplayName);
        Assert.Equal("After", agent.Description);
        Assert.Equal("After", agent.SystemPrompt);
        Assert.Equal(newModelProviderId, agent.ModelProviderId);
        Assert.False(agent.EnableSummary);
        Assert.Empty(agent.Tools);
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
            ModelProviderId = modelProviderId,
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
            """
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.UpdateAgentAsync(agent.Id, request.ToCommand())
        );

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
            ModelProviderId = modelProviderId,
        };
        var service = CreateService(agent, modelProviderIds: [modelProviderId]);
        var request = Deserialize(
            """
            {
              "displayName": "Agent",
              "description": "Description",
              "systemPrompt": "Prompt"
            }
            """
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.UpdateAgentAsync(agent.Id, request.ToCommand())
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains("modelProviderId", exception.Message, StringComparison.Ordinal);
        Assert.Null(agent.UpdateBy);
    }

    [Theory]
    [InlineData(AgentType.System, "tester")]
    [InlineData(AgentType.External, "tester")]
    [InlineData(AgentType.System, "another-user")]
    [InlineData(AgentType.External, "another-user")]
    public async Task CreateAgentAsync_ExistingName_RejectsOnlySameOwner(AgentType type, string existingOwner)
    {
        // Arrange
        var existing = CreateExternalAgent();
        existing.CreateBy = existingOwner;
        var modelProviderId = Guid.CreateVersion7();
        var service = CreateService(existing, modelProviderIds: [modelProviderId]);
        var copy = new Agent
        {
            Name = existing.Name,
            DisplayName = "My copy",
            Type = type,
            ExternalAgentKind = type == AgentType.External ? EngineKind.Pi : EngineKind.Maf,
            ModelProviderId = type == AgentType.System ? modelProviderId : null,
            CreateBy = "tester",
        };

        // Act / Assert
        if (existingOwner == "tester")
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                service.CreateAgentAsync(copy, null, null, null)
            );
            Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
            Assert.Equal("An agent with this name already exists. Choose a different name.", exception.Message);
            Assert.Equal(
                1,
                await _database.Context.Agents.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken)
            );
        }
        else
        {
            var created = await service.CreateAgentAsync(copy, null, null, null);
            Assert.NotNull(created);
            Assert.Equal(existing.Name, created.Name);
            Assert.NotEqual(existing.Id, created.Id);
            Assert.Equal(
                2,
                await _database.Context.Agents.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken)
            );
        }
    }

    [Theory]
    [InlineData(EngineKind.ClaudeCode, ProviderType.OpenAIResponses)]
    [InlineData(EngineKind.Codex, ProviderType.Anthropic)]
    public async Task UpdateAgentAsync_IncompatibleModelProvider_RejectsBeforeMutation(
        EngineKind kind,
        ProviderType providerType
    )
    {
        var agent = CreateExternalAgent();
        agent.ExternalAgentKind = kind;
        var modelProviderId = Guid.CreateVersion7();
        var service = CreateService(agent, [modelProviderId], providerType: providerType);
        var request = new AgentUpdateRequest { ModelProviderId = modelProviderId, DisplayName = "Changed" };

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            service.UpdateAgentAsync(agent.Id, request.ToCommand())
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Null(agent.ModelProviderId);
        Assert.Equal("External Agent", agent.DisplayName);
    }

    [Fact]
    public async Task CreateAgentAsync_IncompatibleModelProvider_DoesNotPersistAgent()
    {
        var existing = CreateExternalAgent();
        var modelProviderId = Guid.CreateVersion7();
        var service = CreateService(existing, [modelProviderId], providerType: ProviderType.OpenAIResponses);
        var agent = CreateExternalAgent();
        agent.Name = "new-agent";
        agent.ModelProviderId = modelProviderId;

        var error = await Assert.ThrowsAsync<AgwException>(() => service.CreateAgentAsync(agent, null, null, null));

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.False(
            await _database.Context.Agents.AnyAsync(item => item.Id == agent.Id, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task GetExternalModelRuntimeConfigurationAsync_UnavailableProvider_FailsClosed()
    {
        var service = CreateService(CreateExternalAgent());
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            service.GetExternalModelRuntimeConfigurationAsync(EngineKind.Pi, Guid.CreateVersion7())
        );
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, error.Code);
        Assert.Null(await service.GetExternalModelRuntimeConfigurationAsync(EngineKind.Pi, null));
    }

    private static Agent CreateExternalAgent() =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = "external-agent",
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.ClaudeCode,
            DisplayName = "External Agent",
            Description = "Original description",
            SystemPrompt = "original-prompt",
            Tools = [new ToolValue { Definition = new WebFetchToolDefinition() }],
            Extra = "{\"original\":true}",
            EnvironmentVariables = new Dictionary<string, string> { ["TOKEN"] = "original" },
            CreateBy = "tester",
        };

    private static AgentUpdateRequest Deserialize(string json) =>
        JsonSerializer.Deserialize<AgentUpdateRequest>(json, JsonOptions)
        ?? throw new Xunit.Sdk.XunitException("Agent update request did not deserialize.");

    private AgentAppService CreateService(
        Agent agent,
        IEnumerable<Guid>? modelProviderIds = null,
        IEnumerable<AgentMcpServerRelation>? mcpRelations = null,
        IEnumerable<AgentSkillRelation>? skillRelations = null,
        IEnumerable<AgentConnectionRelation>? connectionRelations = null,
        ProviderType providerType = ProviderType.Anthropic
    )
    {
        agent.CreateBy ??= "tester";
        var model = new AgwAiModel
        {
            Id = Guid.CreateVersion7(),
            Name = "test-model",
            CreateBy = "tester",
        };
        var provider = new Provider
        {
            Id = Guid.CreateVersion7(),
            ProviderType = providerType,
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
        var connectionRepository = new TestRepository<Connection>();
        var modelProviderRepository = new TestRepository<ModelProviderRelation>(modelProviders, item => item.Id);
        var modelRepository = new TestRepository<AgwAiModel>([model]);
        var providerRepository = new TestRepository<Provider>([provider]);
        var skillRepository = new TestRepository<Skill>();
        var userInfo = new TestUserInfoService();
        _database.Context.McpToolServers.AddRange(
            (mcpRelations ?? []).Select(relation => new McpServer
            {
                Id = relation.McpToolServerId,
                CreateBy = "tester",
            })
        );
        _database.Context.Skills.AddRange(
            (skillRelations ?? []).Select(relation => new Skill { Id = relation.SkillId, CreateBy = "tester" })
        );
        _database.Context.Connections.AddRange(
            (connectionRelations ?? []).Select(relation => new Connection
            {
                Id = relation.ConnectionId,
                Alias = relation.ConnectionId.ToString(),
                CreateBy = "tester",
            })
        );
        _database.Context.Agents.Add(agent);
        _database.Context.AgentMcpToolServers.AddRange(mcpRelations ?? []);
        _database.Context.AgentSkillRelations.AddRange(skillRelations ?? []);
        _database.Context.AgentConnectionRelations.AddRange(connectionRelations ?? []);
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
