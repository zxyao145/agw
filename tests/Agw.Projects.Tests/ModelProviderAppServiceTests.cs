using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Providers.Application;
using Agw.Providers.Domain.Services;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class ModelProviderAppServiceTests
{
    [Fact]
    public async Task DeleteAsync_ReferencedRelation_ThrowsAndPreservesRelation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        var relationId = Guid.CreateVersion7();
        var providerId = Guid.CreateVersion7();
        var modelId = Guid.CreateVersion7();

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.Providers.Add(
                new Provider
                {
                    Id = providerId,
                    Name = "OpenAI",
                    ProviderType = ProviderType.OpenAIChatCompletions,
                    Endpoint = "https://example.test/v1",
                    CreateBy = "seed",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            setupContext.Models.Add(
                new AgwAiModel
                {
                    Id = modelId,
                    Name = "model",
                    CreateBy = "seed",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            setupContext.ModelProviders.Add(
                new ModelProviderRelation
                {
                    Id = relationId,
                    ProviderId = providerId,
                    ModelId = modelId,
                    CreateBy = "seed",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            setupContext.Agents.Add(
                new Agent
                {
                    Id = Guid.CreateVersion7(),
                    DisplayName = "Agent",
                    Name = "agent",
                    Description = string.Empty,
                    SystemPrompt = string.Empty,
                    ModelProviderId = relationId,
                    CreateBy = "seed",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await setupContext.SaveChangesAsync(cancellationToken);
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var usageGuard = new ModelProviderUsageGuard(
                new TestAgentReferenceFacade(
                    new EfRepository<Agent>(deleteContext),
                    new EfRepository<Agentflow>(deleteContext)
                )
            );
            var service = new ModelProviderAppService(
                new EfRepository<ModelProviderRelation>(deleteContext),
                deleteContext,
                new ModelProviderDomainService(TimeProvider.System),
                usageGuard
            );

            var exception = await Assert.ThrowsAsync<AgwException>(() => service.DeleteAsync(relationId));

            Assert.Equal(ErrorCodes.ModelProviderInUse.Code, exception.Code);
        }

        await using var verifyContext = new AgwDbContext(options);
        Assert.True(
            await verifyContext.ModelProviders.AnyAsync(relation => relation.Id == relationId, cancellationToken)
        );
    }
}
