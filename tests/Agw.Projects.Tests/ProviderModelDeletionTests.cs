using System.Security.Claims;
using Agw.Agents.Definitions.Facades;
using Agw.Auth.Application;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Providers;
using Agw.Providers.Application;
using Agw.Providers.Contracts;
using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Services;
using Agw.Providers.Infrastructure;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public sealed class ProviderModelDeletionTests : IDisposable
{
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], authenticationType: "Test")
        )
    );

    public void Dispose() => _userScope.Dispose();

    [Theory]
    [InlineData(EngineKind.ClaudeCode, ProviderType.OpenAIResponses, false)]
    [InlineData(EngineKind.ClaudeCode, ProviderType.Anthropic, true)]
    [InlineData(EngineKind.Pi, ProviderType.OpenAIResponses, true)]
    public async Task UpdateProviderAsync_ExternalAgentReference_EnforcesProviderCompatibility(
        EngineKind kind,
        ProviderType providerType,
        bool supported
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var provider = CreateProvider(Guid.CreateVersion7());
        provider.ProviderType = ProviderType.Anthropic;
        var model = CreateModel();
        var relation = CreateRelation(provider.Id, model.Id);
        await using (var context = new AgwDbContext(options))
        {
            await context.Database.EnsureCreatedAsync(cancellationToken);
            context.AddRange(
                provider,
                model,
                relation,
                new Agent
                {
                    Id = Guid.CreateVersion7(),
                    Name = "external",
                    DisplayName = "External",
                    CreateBy = "tester",
                    Type = AgentType.External,
                    ExternalAgentKind = kind,
                    ModelProviderId = relation.Id,
                }
            );
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var updateContext = new AgwDbContext(options);
        var service = CreateProviderService(updateContext, new UserInfoService());
        var request = new ProviderUpdateRequest("updated", providerType, null, "https://example.test", [], null);
        if (supported)
        {
            Assert.NotNull(await service.UpdateAsync(provider.Id, request, cancellationToken));
        }
        else
        {
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                service.UpdateAsync(provider.Id, request, cancellationToken)
            );
            Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        }

        await using var verification = new AgwDbContext(options);
        var persisted = await verification.Providers.SingleAsync(cancellationToken);
        Assert.Equal(supported ? providerType : ProviderType.Anthropic, persisted.ProviderType);
        Assert.Equal(supported ? "updated" : provider.Name, persisted.Name);
    }

    [Theory]
    [InlineData("agent-model")]
    [InlineData("agent-summary")]
    [InlineData("agentflow-summary")]
    public async Task DeleteProviderAsync_ReferencedRelation_ThrowsAndKeepsProviderAndRelation(string usage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var providerId = Guid.CreateVersion7();
        var model = CreateModel();
        var relation = CreateRelation(providerId, model.Id);

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.AddRange(CreateProvider(providerId), model, relation);
            AddReference(setupContext, relation.Id, usage);
            await setupContext.SaveChangesAsync(cancellationToken);
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var user = new UserInfoService();
            var service = CreateProviderService(deleteContext, user);

            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                service.DeleteAsync(providerId, TestContext.Current.CancellationToken)
            );

            Assert.Equal(ErrorCodes.ModelProviderInUse.Code, exception.Code);
        }

        await using var verifyContext = new AgwDbContext(options);
        Assert.True(
            await verifyContext
                .Providers.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == providerId, cancellationToken)
        );
        Assert.True(
            await verifyContext
                .ModelProviders.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == relation.Id, cancellationToken)
        );
    }

    [Fact]
    public async Task DeleteProviderAsync_UnreferencedProvider_RemovesRelationsCredentialsAndProvider()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var providerId = Guid.CreateVersion7();
        var model = CreateModel();
        var relation = CreateRelation(providerId, model.Id);

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.AddRange(CreateProvider(providerId), model, relation);
            setupContext.ProviderAuthConfigs.Add(
                new ProviderAuthConfig
                {
                    Id = Guid.CreateVersion7(),
                    ProviderId = providerId,
                    ApiKey = "secret",
                    CreateBy = "tester",
                }
            );
            await setupContext.SaveChangesAsync(cancellationToken);
        }
        await using (var deleteContext = new AgwDbContext(options))
        {
            var user = new UserInfoService();
            var service = CreateProviderService(deleteContext, user);
            await AssertBlockedByDefinitionLockAsync(
                async () => Assert.True(await service.DeleteAsync(providerId)),
                async () =>
                {
                    await using var assertContext = new AgwDbContext(options);
                    Assert.True(
                        await assertContext
                            .Providers.IgnoreQueryFilters()
                            .AnyAsync(item => item.Id == providerId, cancellationToken)
                    );
                    Assert.True(
                        await assertContext
                            .ProviderAuthConfigs.IgnoreQueryFilters()
                            .AnyAsync(item => item.ProviderId == providerId, cancellationToken)
                    );
                    Assert.True(
                        await assertContext
                            .ModelProviders.IgnoreQueryFilters()
                            .AnyAsync(item => item.ProviderId == providerId, cancellationToken)
                    );
                },
                cancellationToken
            );
        }

        await using var verifyContext = new AgwDbContext(options);
        Assert.False(
            await verifyContext
                .Providers.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == providerId, cancellationToken)
        );
        Assert.False(
            await verifyContext
                .ProviderAuthConfigs.IgnoreQueryFilters()
                .AnyAsync(item => item.ProviderId == providerId, cancellationToken)
        );
        Assert.False(
            await verifyContext
                .ModelProviders.IgnoreQueryFilters()
                .AnyAsync(item => item.ProviderId == providerId, cancellationToken)
        );
        Assert.True(
            await verifyContext.Models.IgnoreQueryFilters().AnyAsync(item => item.Id == model.Id, cancellationToken)
        );
    }

    [Fact]
    public async Task DeleteProviderAsync_RootDeleteRejected_RollsBackRelatedCleanup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var providerId = Guid.CreateVersion7();
        var model = CreateModel();
        var relation = CreateRelation(providerId, model.Id);

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.AddRange(CreateProvider(providerId), model, relation);
            setupContext.ProviderAuthConfigs.Add(
                new ProviderAuthConfig
                {
                    Id = Guid.CreateVersion7(),
                    ProviderId = providerId,
                    ApiKey = "secret",
                    CreateBy = "tester",
                }
            );
            await setupContext.SaveChangesAsync(cancellationToken);
            await setupContext.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER reject_provider_delete BEFORE DELETE ON provider BEGIN SELECT RAISE(ABORT, 'provider delete rejected'); END;",
                cancellationToken
            );
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var service = CreateProviderService(deleteContext, new UserInfoService());

            await Assert.ThrowsAsync<SqliteException>(() =>
                service.DeleteAsync(providerId, TestContext.Current.CancellationToken)
            );
        }

        await using var verifyContext = new AgwDbContext(options);
        Assert.True(
            await verifyContext
                .Providers.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == providerId, cancellationToken)
        );
        Assert.True(
            await verifyContext
                .ProviderAuthConfigs.IgnoreQueryFilters()
                .AnyAsync(config => config.ProviderId == providerId, cancellationToken)
        );
        Assert.True(
            await verifyContext
                .ModelProviders.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == relation.Id, cancellationToken)
        );
    }

    [Theory]
    [InlineData("agent-model")]
    [InlineData("agent-summary")]
    [InlineData("agentflow-summary")]
    public async Task DeleteModelAsync_ReferencedRelation_ThrowsAndKeepsModelAndRelation(string usage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var providerId = Guid.CreateVersion7();
        var model = CreateModel();
        var relation = CreateRelation(providerId, model.Id);

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.AddRange(CreateProvider(providerId), model, relation);
            AddReference(setupContext, relation.Id, usage);
            await setupContext.SaveChangesAsync(cancellationToken);
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var user = new UserInfoService();
            var service = CreateModelService(deleteContext, user);

            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                service.DeleteAsync(model.Id, TestContext.Current.CancellationToken)
            );

            Assert.Equal(ErrorCodes.ModelProviderInUse.Code, exception.Code);
        }

        await using var verifyContext = new AgwDbContext(options);
        Assert.True(
            await verifyContext.Models.IgnoreQueryFilters().AnyAsync(item => item.Id == model.Id, cancellationToken)
        );
        Assert.True(
            await verifyContext
                .ModelProviders.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == relation.Id, cancellationToken)
        );
    }

    [Fact]
    public async Task DeleteModelAsync_UnreferencedModel_RemovesRelationsAndModel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var providerId = Guid.CreateVersion7();
        var model = CreateModel();
        var relation = CreateRelation(providerId, model.Id);

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.AddRange(CreateProvider(providerId), model, relation);
            await setupContext.SaveChangesAsync(cancellationToken);
        }
        await using (var deleteContext = new AgwDbContext(options))
        {
            var user = new UserInfoService();
            var service = CreateModelService(deleteContext, user);
            await AssertBlockedByDefinitionLockAsync(
                async () => Assert.True(await service.DeleteAsync(model.Id)),
                async () =>
                {
                    await using var assertContext = new AgwDbContext(options);
                    Assert.True(
                        await assertContext
                            .Models.IgnoreQueryFilters()
                            .AnyAsync(item => item.Id == model.Id, cancellationToken)
                    );
                    Assert.True(
                        await assertContext
                            .ModelProviders.IgnoreQueryFilters()
                            .AnyAsync(item => item.Id == relation.Id, cancellationToken)
                    );
                },
                cancellationToken
            );
        }

        await using var verifyContext = new AgwDbContext(options);
        Assert.False(
            await verifyContext.Models.IgnoreQueryFilters().AnyAsync(item => item.Id == model.Id, cancellationToken)
        );
        Assert.False(
            await verifyContext
                .ModelProviders.IgnoreQueryFilters()
                .AnyAsync(item => item.ModelId == model.Id, cancellationToken)
        );
        Assert.True(
            await verifyContext
                .Providers.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == providerId, cancellationToken)
        );
    }

    [Fact]
    public async Task DeleteModelAsync_RootDeleteRejected_RollsBackRelationCleanup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var provider = CreateProvider(Guid.CreateVersion7());
        var model = CreateModel();
        var relation = CreateRelation(provider.Id, model.Id);

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.AddRange(provider, model, relation);
            await setupContext.SaveChangesAsync(cancellationToken);
            await setupContext.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER reject_model_delete BEFORE DELETE ON model BEGIN SELECT RAISE(ABORT, 'model delete rejected'); END;",
                cancellationToken
            );
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var service = CreateModelService(deleteContext, new UserInfoService());

            await Assert.ThrowsAsync<SqliteException>(() =>
                service.DeleteAsync(model.Id, TestContext.Current.CancellationToken)
            );
        }

        await using var verifyContext = new AgwDbContext(options);
        Assert.True(
            await verifyContext.Models.IgnoreQueryFilters().AnyAsync(item => item.Id == model.Id, cancellationToken)
        );
        Assert.True(
            await verifyContext
                .ModelProviders.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == relation.Id, cancellationToken)
        );
        Assert.True(
            await verifyContext
                .Providers.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == provider.Id, cancellationToken)
        );
    }

    [Fact]
    public async Task CreateProviderAsync_WithModelSelection_WaitsForAgentDefinitionLock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var model = CreateModel();

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.Models.Add(model);
            await setupContext.SaveChangesAsync(cancellationToken);
        }

        await using var createContext = new AgwDbContext(options);
        var service = CreateProviderService(createContext, new UserInfoService());
        Provider? provider = null;
        await AssertBlockedByDefinitionLockAsync(
            async () =>
                provider = await service.CreateAsync(
                    new ProviderCreateRequest(
                        "provider",
                        ProviderType.OpenAIResponses,
                        null,
                        "https://example.test",
                        null,
                        [model.Name]
                    )
                ),
            async () =>
            {
                await using var assertContext = new AgwDbContext(options);
                Assert.Empty(await assertContext.Providers.IgnoreQueryFilters().ToListAsync(cancellationToken));
                Assert.Empty(await assertContext.ModelProviders.IgnoreQueryFilters().ToListAsync(cancellationToken));
            },
            cancellationToken
        );

        Assert.NotNull(provider);
        Assert.True(
            await createContext
                .ModelProviders.IgnoreQueryFilters()
                .AnyAsync(
                    relation => relation.ProviderId == provider.Id && relation.ModelId == model.Id,
                    cancellationToken
                )
        );
    }

    [Fact]
    public async Task UpdateProviderAsync_WithModelSelection_WaitsForAgentDefinitionLock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var providerId = Guid.CreateVersion7();
        var oldModel = CreateModel("old-model");
        var newModel = CreateModel("new-model");
        var oldRelation = CreateRelation(providerId, oldModel.Id);

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.AddRange(CreateProvider(providerId), oldModel, newModel, oldRelation);
            await setupContext.SaveChangesAsync(cancellationToken);
        }

        await using var updateContext = new AgwDbContext(options);
        var service = CreateProviderService(updateContext, new UserInfoService());
        await AssertBlockedByDefinitionLockAsync(
            async () =>
                Assert.NotNull(
                    await service.UpdateAsync(
                        providerId,
                        new ProviderUpdateRequest(
                            "updated-provider",
                            ProviderType.OpenAIResponses,
                            null,
                            "https://example.test",
                            [],
                            [newModel.Name]
                        )
                    )
                ),
            async () =>
            {
                await using var assertContext = new AgwDbContext(options);
                var persistedProvider = await assertContext
                    .Providers.IgnoreQueryFilters()
                    .SingleAsync(item => item.Id == providerId, cancellationToken);
                Assert.Equal("provider", persistedProvider.Name);
                var modelIds = await assertContext
                    .ModelProviders.IgnoreQueryFilters()
                    .Where(relation => relation.ProviderId == providerId)
                    .Select(relation => relation.ModelId)
                    .ToListAsync(cancellationToken);
                Assert.Equal([oldModel.Id], modelIds);
            },
            cancellationToken
        );

        var modelIds = await updateContext
            .ModelProviders.IgnoreQueryFilters()
            .Where(relation => relation.ProviderId == providerId)
            .Select(relation => relation.ModelId)
            .ToListAsync(cancellationToken);
        Assert.Equal([newModel.Id], modelIds);
    }

    [Fact]
    public async Task CreateModelProviderAsync_DefinitionLockHeld_WaitsForRelease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var provider = CreateProvider(Guid.CreateVersion7());
        var model = CreateModel();

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.AddRange(provider, model);
            await setupContext.SaveChangesAsync(cancellationToken);
        }

        await using var createContext = new AgwDbContext(options);
        var user = new UserInfoService();
        var service = CreateModelProviderService(createContext, user);
        ModelProviderRelation? relation = null;
        await AssertBlockedByDefinitionLockAsync(
            async () =>
                relation = await service.CreateAsync(
                    new ModelProviderCreateRequest(model.Id, provider.Id, 0, 0, 0, 0, 0)
                ),
            async () =>
            {
                await using var assertContext = new AgwDbContext(options);
                Assert.False(
                    await assertContext
                        .ModelProviders.IgnoreQueryFilters()
                        .AnyAsync(item => item.ProviderId == provider.Id && item.ModelId == model.Id, cancellationToken)
                );
            },
            cancellationToken
        );

        Assert.NotNull(relation);
        Assert.True(
            await createContext
                .ModelProviders.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == relation.Id, cancellationToken)
        );
    }

    [Fact]
    public async Task DeleteModelProviderAsync_DefinitionLockHeld_WaitsForRelease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var provider = CreateProvider(Guid.CreateVersion7());
        var model = CreateModel();
        var relation = CreateRelation(provider.Id, model.Id);

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.AddRange(provider, model, relation);
            await setupContext.SaveChangesAsync(cancellationToken);
        }

        await using var deleteContext = new AgwDbContext(options);
        var user = new UserInfoService();
        var service = CreateModelProviderService(deleteContext, user);
        await AssertBlockedByDefinitionLockAsync(
            async () => Assert.True(await service.DeleteAsync(relation.Id)),
            async () =>
            {
                await using var assertContext = new AgwDbContext(options);
                Assert.True(
                    await assertContext
                        .ModelProviders.IgnoreQueryFilters()
                        .AnyAsync(item => item.Id == relation.Id, cancellationToken)
                );
            },
            cancellationToken
        );

        Assert.False(
            await deleteContext
                .ModelProviders.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == relation.Id, cancellationToken)
        );
    }

    private static ProviderAppService CreateProviderService(AgwDbContext dbContext, IUserInfoService user)
    {
        var bindingDomainService = CreateBindingDomainService(dbContext, user);
        var deletionCoordinator = new ProviderModelDeletionCoordinator(dbContext, InMemoryApplicationLock.Shared, user);
        return new ProviderAppService(
            dbContext,
            bindingDomainService,
            user,
            InMemoryApplicationLock.Shared,
            deletionCoordinator,
            new AgentCatalogFacade(dbContext, user)
        );
    }

    private static ModelAppService CreateModelService(AgwDbContext dbContext, IUserInfoService user)
    {
        var bindingDomainService = CreateBindingDomainService(dbContext, user);
        var deletionCoordinator = new ProviderModelDeletionCoordinator(dbContext, InMemoryApplicationLock.Shared, user);
        return new ModelAppService(dbContext, user, bindingDomainService, deletionCoordinator);
    }

    private static ModelProviderAppService CreateModelProviderService(AgwDbContext dbContext, IUserInfoService user) =>
        new(dbContext, CreateBindingDomainService(dbContext, user), user, InMemoryApplicationLock.Shared);

    private static ProviderModelBindingDomainService CreateBindingDomainService(
        AgwDbContext dbContext,
        IUserInfoService user
    ) => new(new ProviderModelRepository(dbContext, user), new AgentCatalogFacade(dbContext, user));

    private static async Task AssertBlockedByDefinitionLockAsync(
        Func<Task> action,
        Func<Task> assertUnchanged,
        CancellationToken cancellationToken
    )
    {
        Task operation;
        await using (
            var lease = await InMemoryApplicationLock.Shared.AcquireAsync(
                AgentDefinitionLock.GetResourceName("tester"),
                cancellationToken
            )
        )
        {
            operation = action();
            var timeout = Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            Assert.NotSame(operation, await Task.WhenAny(operation, timeout));
            await assertUnchanged();
        }

        await operation;
    }

    private static void AddReference(AgwDbContext dbContext, Guid relationId, string usage)
    {
        if (usage == "agentflow-summary")
        {
            dbContext.Agentflows.Add(
                new Agentflow
                {
                    Id = Guid.CreateVersion7(),
                    Name = "flow",
                    SystemPrompt = string.Empty,
                    SummaryModelProviderId = relationId,
                    CreateBy = "tester",
                }
            );
            return;
        }

        dbContext.Agents.Add(
            new Agent
            {
                Id = Guid.CreateVersion7(),
                DisplayName = "Agent",
                Name = $"agent-{usage}",
                Description = string.Empty,
                SystemPrompt = string.Empty,
                ModelProviderId = usage == "agent-model" ? relationId : null,
                SummaryModelProviderId = usage == "agent-summary" ? relationId : null,
                CreateBy = "tester",
            }
        );
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).UseSnakeCaseNamingConvention().Options;

    private static Provider CreateProvider(Guid id) =>
        new()
        {
            Id = id,
            Name = "provider",
            Endpoint = "https://example.test",
            CreateBy = "tester",
        };

    private static AgwAiModel CreateModel() => CreateModel("model");

    private static AgwAiModel CreateModel(string name) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            MaxContextWindowTokens = AgwAiModel.DefaultMaxContextWindowTokens,
            MaxOutputTokens = AgwAiModel.DefaultMaxOutputTokens,
            CreateBy = "tester",
        };

    private static ModelProviderRelation CreateRelation(Guid providerId, Guid modelId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProviderId = providerId,
            ModelId = modelId,
            CreateBy = "tester",
        };
}
