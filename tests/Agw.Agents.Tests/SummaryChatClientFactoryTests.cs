using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Summaries;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Data.Entities.Providers;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class SummaryChatClientFactoryTests
{
    [Fact]
    public async Task CreateAsync_OpenAiRuntimeConfiguration_ReturnsOneShotChatClient()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        var provider = new Provider
        {
            Id = Guid.NewGuid(),
            Name = "OpenAI",
            ProviderType = ProviderType.OpenAI,
            Endpoint = "https://example.invalid",
            AuthConfigs =
            [
                new ProviderAuthConfig
                {
                    Id = Guid.NewGuid(),
                    AuthType = ProviderAuthType.ApiKey,
                    ApiKey = "test-key",
                    Enable = true,
                }
            ]
        };
        var model = new LlmModel { Id = Guid.NewGuid(), Name = "test-model" };
        var modelProvider = new ModelProviderRelation
        {
            Id = Guid.NewGuid(),
            ModelId = model.Id,
            ProviderId = provider.Id,
        };
        dbContext.AddRange(provider, model, modelProvider);
        await dbContext.SaveChangesAsync(cancellationToken);

        var appService = new AgentAppService(
            null!,
            null!,
            null!,
            null!,
            new EfRepository<ModelProviderRelation>(dbContext),
            new EfRepository<LlmModel>(dbContext),
            new EfRepository<Provider>(dbContext),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        var factory = new SummaryChatClientFactory(
            appService,
            NullLogger<SummaryChatClientFactory>.Instance);

        using var client = await factory.CreateAsync(modelProvider.Id, cancellationToken);

        Assert.NotNull(client);
        Assert.NotNull(client.GetService<ChatClientMetadata>());
    }
}
