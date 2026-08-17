using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Providers.Application;
using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Services;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class ProviderAppServiceTests
{
    [Fact]
    public void ProviderAuthType_OnlySupportsApiKey()
    {
        Assert.Equal([nameof(ProviderAuthType.ApiKey)], Enum.GetNames<ProviderAuthType>());
    }

    [Fact]
    public async Task CreateAsync_WithModelNames_ReusesExistingAndCreatesMissingModels()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.Models.Add(new AgwAiModel
            {
                Id = Guid.CreateVersion7(),
                Name = "existing-model",
                MaxContextWindowTokens = 8192,
                MaxOutputTokens = 2048,
                CreateBy = "seed",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            await setupContext.SaveChangesAsync(cancellationToken);
        }

        await using (var createContext = new AgwDbContext(options))
        {
            var service = CreateService(createContext);
            await service.CreateAsync(
                new ProviderCreateRequest(
                    Name: "OpenAI",
                    ProviderType: ProviderType.OpenAIChatCompletions,
                    Description: null,
                    Endpoint: "https://example.test/v1",
                    AuthConfigs: null,
                    ModelNames: [" existing-model ", "new-model", "new-model"]),
                "tester");
        }

        await using var verifyContext = new AgwDbContext(options);
        var provider = await verifyContext.Providers.SingleAsync(cancellationToken);
        var relatedModels = await verifyContext.ModelProviders
            .Where(relation => relation.ProviderId == provider.Id)
            .Join(
                verifyContext.Models,
                relation => relation.ModelId,
                model => model.Id,
                (_, model) => model)
            .OrderBy(model => model.Name)
            .ToListAsync(cancellationToken);

        Assert.Equal(["existing-model", "new-model"], relatedModels.Select(model => model.Name));
        var existingModel = relatedModels.Single(model => model.Name == "existing-model");
        Assert.Equal(8192, existingModel.MaxContextWindowTokens);
        Assert.Equal(2048, existingModel.MaxOutputTokens);
        var newModel = relatedModels.Single(model => model.Name == "new-model");
        Assert.Equal(AgwAiModel.DefaultMaxContextWindowTokens, newModel.MaxContextWindowTokens);
        Assert.Equal(AgwAiModel.DefaultMaxOutputTokens, newModel.MaxOutputTokens);
        Assert.All(
            await verifyContext.ModelProviders.ToListAsync(cancellationToken),
            relation =>
            {
                Assert.Equal(0, relation.InputPrice);
                Assert.Equal(0, relation.OutputPrice);
                Assert.Equal(0, relation.CacheRead);
                Assert.Equal(0, relation.CacheWrite);
                Assert.Equal(0, relation.RpsLimit);
            });
    }

    [Fact]
    public async Task UpdateAsync_WithModelNames_SynchronizesCompleteRelationSet()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var providerId = Guid.CreateVersion7();

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            var keepModel = CreateModel("keep-model");
            var removeModel = CreateModel("remove-model");
            var addModel = CreateModel("add-model");
            setupContext.Models.AddRange(keepModel, removeModel, addModel);
            setupContext.Providers.Add(CreateProvider(providerId));
            setupContext.ModelProviders.AddRange(
                CreateRelation(providerId, keepModel.Id),
                CreateRelation(providerId, removeModel.Id));
            await setupContext.SaveChangesAsync(cancellationToken);
        }

        await using (var updateContext = new AgwDbContext(options))
        {
            var service = CreateService(updateContext);
            await service.UpdateAsync(
                providerId,
                new ProviderUpdateRequest(
                    Name: "OpenAI",
                    ProviderType: ProviderType.OpenAIChatCompletions,
                    Description: "updated",
                    Endpoint: "https://example.test/v1",
                    AuthConfigs: null,
                    ModelNames: ["keep-model", "add-model"]),
                "tester");
        }

        await using var verifyContext = new AgwDbContext(options);
        var relatedNames = await verifyContext.ModelProviders
            .Where(relation => relation.ProviderId == providerId)
            .Join(
                verifyContext.Models,
                relation => relation.ModelId,
                model => model.Id,
                (_, model) => model.Name)
            .OrderBy(name => name)
            .ToListAsync(cancellationToken);

        Assert.Equal(["add-model", "keep-model"], relatedNames);
    }

    [Fact]
    public async Task UpdateAsync_WithoutModelNames_PreservesExistingRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var providerId = Guid.CreateVersion7();
        var model = CreateModel("existing-model");

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.Models.Add(model);
            setupContext.Providers.Add(CreateProvider(providerId));
            setupContext.ModelProviders.Add(CreateRelation(providerId, model.Id));
            await setupContext.SaveChangesAsync(cancellationToken);
        }

        await using (var updateContext = new AgwDbContext(options))
        {
            var service = CreateService(updateContext);
            await service.UpdateAsync(
                providerId,
                new ProviderUpdateRequest(
                    Name: "OpenAI",
                    ProviderType: ProviderType.OpenAIChatCompletions,
                    Description: "updated",
                    Endpoint: "https://example.test/v1",
                    AuthConfigs: null,
                    ModelNames: null),
                "tester");
        }

        await using var verifyContext = new AgwDbContext(options);
        Assert.True(await verifyContext.ModelProviders.AnyAsync(
            relation => relation.ProviderId == providerId && relation.ModelId == model.Id,
            cancellationToken));
    }

    [Theory]
    [InlineData("agent-model")]
    [InlineData("agent-summary")]
    [InlineData("agentflow-summary")]
    public async Task UpdateAsync_RemovingReferencedRelation_ThrowsWithoutPartialUpdate(string usage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        var providerId = Guid.CreateVersion7();
        var model = CreateModel("used-model");
        var relation = CreateRelation(providerId, model.Id);

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.Models.Add(model);
            setupContext.Providers.Add(CreateProvider(providerId));
            setupContext.ModelProviders.Add(relation);
            if (usage == "agentflow-summary")
            {
                setupContext.Agentflows.Add(new Agentflow
                {
                    Id = Guid.CreateVersion7(),
                    Name = "flow",
                    SystemPrompt = string.Empty,
                    SummaryModelProviderId = relation.Id,
                    CreateBy = "seed",
                    CreateTime = TimeProvider.System.GetUtcNow()
                });
            }
            else
            {
                setupContext.Agents.Add(new Agent
                {
                    Id = Guid.CreateVersion7(),
                    DisplayName = "Agent",
                    Name = $"agent-{usage}",
                    Description = string.Empty,
                    SystemPrompt = string.Empty,
                    ModelProviderId = usage == "agent-model" ? relation.Id : null,
                    SummaryModelProviderId = usage == "agent-summary" ? relation.Id : null,
                    CreateBy = "seed",
                    CreateTime = TimeProvider.System.GetUtcNow()
                });
            }

            await setupContext.SaveChangesAsync(cancellationToken);
        }

        await using (var updateContext = new AgwDbContext(options))
        {
            var service = CreateService(updateContext);
            var exception = await Assert.ThrowsAsync<AgwException>(() => service.UpdateAsync(
                providerId,
                new ProviderUpdateRequest(
                    Name: "Changed",
                    ProviderType: ProviderType.OpenAIChatCompletions,
                    Description: "changed",
                    Endpoint: "https://changed.test/v1",
                    AuthConfigs:
                    [
                        new ProviderAuthConfigRequest(
                            ProviderAuthType.ApiKey,
                            ApiKey: "new-key",
                            EnvKey: null,
                            Enable: true)
                    ],
                    ModelNames: []),
                "tester"));

            Assert.Equal(ErrorCodes.ModelProviderInUse.Code, exception.Code);
        }

        await using var verifyContext = new AgwDbContext(options);
        var provider = await verifyContext.Providers
            .Include(item => item.AuthConfigs)
            .SingleAsync(item => item.Id == providerId, cancellationToken);
        Assert.Equal("OpenAI", provider.Name);
        Assert.Equal("Original description", provider.Description);
        Assert.Empty(provider.AuthConfigs);
        Assert.True(await verifyContext.ModelProviders.AnyAsync(
            item => item.Id == relation.Id,
            cancellationToken));
        Assert.Single(await verifyContext.Models.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_WhenDatabaseHasNoForeignKeys_DeletesAuthConfigs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var providerId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Providers.Add(new Provider
            {
                Id = providerId,
                Name = "OpenAI",
                ProviderType = ProviderType.OpenAIChatCompletions,
                Endpoint = "https://api.openai.com/v1",
                CreateBy = "seed",
                CreateTime = TimeProvider.System.GetUtcNow(),
                AuthConfigs =
                [
                    new ProviderAuthConfig
                    {
                        Id = Guid.CreateVersion7(),
                        ProviderId = providerId,
                        AuthType = ProviderAuthType.ApiKey,
                        ApiKey = "test-key",
                        Enable = true,
                        CreateBy = "seed",
                        CreateTime = TimeProvider.System.GetUtcNow()
                    }
                ]
            });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var service = CreateService(deleteContext);

            var deleted = await service.DeleteAsync(providerId);

            Assert.True(deleted);
        }

        await using var verifyContext = new AgwDbContext(options);
        Assert.False(await verifyContext.Providers.AnyAsync(x => x.Id == providerId, cancellationToken));
        Assert.False(await verifyContext.ProviderAuthConfigs.AnyAsync(x => x.ProviderId == providerId, cancellationToken));
    }

    [Fact]
    public async Task UpdateAsync_WhenReplacingAuthConfigs_PersistsNewConfigs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var providerId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Providers.Add(new Provider
            {
                Id = providerId,
                Name = "OpenAI",
                ProviderType = ProviderType.OpenAIChatCompletions,
                Endpoint = "https://api.openai.com/v1",
                Description = "Original description",
                CreateBy = "seed",
                CreateTime = TimeProvider.System.GetUtcNow(),
                AuthConfigs =
                [
                    new ProviderAuthConfig
                    {
                        Id = Guid.CreateVersion7(),
                        ProviderId = providerId,
                        AuthType = ProviderAuthType.ApiKey,
                        ApiKey = "old-key",
                        Enable = true,
                        CreateBy = "seed",
                        CreateTime = TimeProvider.System.GetUtcNow()
                    }
                ]
            });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var updateContext = new AgwDbContext(options))
        {
            var service = CreateService(updateContext);

            var updated = await service.UpdateAsync(
                providerId,
                new ProviderUpdateRequest(
                    Name: "OpenAI",
                    ProviderType: ProviderType.OpenAIChatCompletions,
                    Description: "Updated description",
                    Endpoint: "https://api.openai.com/v1",
                    AuthConfigs:
                    [
                        new ProviderAuthConfigRequest(
                            ProviderAuthType.ApiKey,
                            ApiKey: "new-key",
                            EnvKey: null,
                            Enable: true)
                    ]),
                "tester");

            Assert.NotNull(updated);
        }

        await using var verifyContext = new AgwDbContext(options);
        var provider = await verifyContext.Providers
            .Include(x => x.AuthConfigs)
            .SingleAsync(x => x.Id == providerId, cancellationToken);

        var authConfig = Assert.Single(provider.AuthConfigs);
        Assert.Equal("Updated description", provider.Description);
        Assert.Equal(ProviderAuthType.ApiKey, authConfig.AuthType);
        Assert.Null(authConfig.EnvName);
        Assert.Equal("new-key", authConfig.ApiKey);
    }

    private static ProviderAppService CreateService(AgwDbContext dbContext)
    {
        return new ProviderAppService(
            new EfRepository<Provider>(dbContext),
            new EfRepository<AgwAiModel>(dbContext),
            new EfRepository<ModelProviderRelation>(dbContext),
            dbContext,
            new ProviderDomainService(TimeProvider.System),
            new ModelDomainService(TimeProvider.System),
            new ModelProviderDomainService(TimeProvider.System),
            new ModelProviderUsageGuard(
                new EfRepository<Agent>(dbContext),
                new EfRepository<Agentflow>(dbContext)));
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

    private static Provider CreateProvider(Guid providerId) => new()
    {
        Id = providerId,
        Name = "OpenAI",
        ProviderType = ProviderType.OpenAIChatCompletions,
        Endpoint = "https://example.test/v1",
        Description = "Original description",
        CreateBy = "seed",
        CreateTime = TimeProvider.System.GetUtcNow()
    };

    private static AgwAiModel CreateModel(string name) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = name,
        MaxContextWindowTokens = AgwAiModel.DefaultMaxContextWindowTokens,
        MaxOutputTokens = AgwAiModel.DefaultMaxOutputTokens,
        CreateBy = "seed",
        CreateTime = TimeProvider.System.GetUtcNow()
    };

    private static ModelProviderRelation CreateRelation(Guid providerId, Guid modelId) => new()
    {
        Id = Guid.CreateVersion7(),
        ProviderId = providerId,
        ModelId = modelId,
        CreateBy = "seed",
        CreateTime = TimeProvider.System.GetUtcNow()
    };
}
