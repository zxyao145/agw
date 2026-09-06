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
            Id = Guid.CreateVersion7(),
            Name = "OpenAI",
            ProviderType = ProviderType.OpenAIChatCompletions,
            Endpoint = "https://example.invalid",
            CreateBy = "tester",
            AuthConfigs =
            [
                new ProviderAuthConfig
                {
                    Id = Guid.CreateVersion7(),
                    AuthType = ProviderAuthType.ApiKey,
                    ApiKey = "test-key",
                    Enable = true,
                    CreateBy = "tester",
                },
            ],
        };
        var model = new AgwAiModel
        {
            Id = Guid.CreateVersion7(),
            Name = "test-model",
            CreateBy = "tester",
        };
        var modelProvider = new ModelProviderRelation
        {
            Id = Guid.CreateVersion7(),
            ModelId = model.Id,
            ProviderId = provider.Id,
            CreateBy = "tester",
        };
        dbContext.AddRange(provider, model, modelProvider);
        await dbContext.SaveChangesAsync(cancellationToken);

        var userInfo = new TestUserInfoService();
        var appService = new AgentAppService(
            null!,
            null!,
            new TestModelProviderReferenceFacade(
                new EfRepository<ModelProviderRelation>(dbContext),
                new EfRepository<AgwAiModel>(dbContext),
                new EfRepository<Provider>(dbContext),
                userInfo
            ),
            null!,
            userInfo,
            null!
        );
        var factory = new SummaryChatClientFactory(appService, NullLogger<SummaryChatClientFactory>.Instance);

        using var client = await factory.CreateAsync(modelProvider.Id, cancellationToken);

        Assert.NotNull(client);
        Assert.NotNull(client.GetService<ChatClientMetadata>());
    }
}
