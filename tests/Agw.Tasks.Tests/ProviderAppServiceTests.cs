using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Providers.Application;
using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Services;
using Agw.Shared.Data.Entities.Providers;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Tests;

public class ProviderAppServiceTests
{
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

        var providerId = Guid.NewGuid();
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
                        Id = Guid.NewGuid(),
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

        var providerId = Guid.NewGuid();
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
                        Id = Guid.NewGuid(),
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
                            ProviderAuthType.EnvVariable,
                            ApiKey: null,
                            EnvKey: "OPENAI_API_KEY",
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
        Assert.Equal(ProviderAuthType.EnvVariable, authConfig.AuthType);
        Assert.Equal("OPENAI_API_KEY", authConfig.EnvName);
        Assert.Null(authConfig.ApiKey);
    }

    private static ProviderAppService CreateService(AgwDbContext dbContext)
    {
        return new ProviderAppService(
            new EfRepository<Provider>(dbContext),
            new UnitOfWork(dbContext),
            new ProviderDomainService(TimeProvider.System));
    }
}
