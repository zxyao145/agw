using Agw.Domain.Services;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Manager.Api.Contracts;
using Agw.Providers.Application;
using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Entities;
using Agw.Providers.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Tests;

public class ProviderAppServiceTests
{
    [Fact]
    public async Task UpdateAsync_WhenReplacingAuthConfigs_PersistsNewConfigs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new LlmDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var providerId = Guid.NewGuid();
        await using (var seedContext = new LlmDbContext(options))
        {
            seedContext.Providers.Add(new Provider
            {
                Id = providerId,
                Name = "OpenAI",
                ProviderType = ProviderType.OpenAI,
                Endpoint = "https://api.openai.com/v1",
                Description = "Original description",
                CreateBy = "seed",
                CreateTime = DateTime.UtcNow,
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
                        CreateTime = DateTime.UtcNow
                    }
                ]
            });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var updateContext = new LlmDbContext(options))
        {
            var service = CreateService(updateContext);

            var updated = await service.UpdateAsync(
                providerId,
                new ProviderUpdateRequest(
                    Name: "OpenAI",
                    ProviderType: ProviderType.OpenAI,
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

        await using var verifyContext = new LlmDbContext(options);
        var provider = await verifyContext.Providers
            .Include(x => x.AuthConfigs)
            .SingleAsync(x => x.Id == providerId, cancellationToken);

        var authConfig = Assert.Single(provider.AuthConfigs);
        Assert.Equal("Updated description", provider.Description);
        Assert.Equal(ProviderAuthType.EnvVariable, authConfig.AuthType);
        Assert.Equal("OPENAI_API_KEY", authConfig.EnvName);
        Assert.Null(authConfig.ApiKey);
    }

    private static ProviderAppService CreateService(LlmDbContext dbContext)
    {
        return new ProviderAppService(
            new EfRepository<Provider>(dbContext),
            new UnitOfWork(dbContext),
            new ProviderDomainService());
    }
}
