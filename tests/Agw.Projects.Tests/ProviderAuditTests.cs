using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Infrastructure.Repositories;
using Agw.Providers.Application;
using Agw.Providers.Contracts.Manager;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Providers;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class ProviderAuditTests
{
    [Fact]
    public async Task CreateAndUpdateAsync_ProviderModelAndRelation_PersistOwnerAndRefreshAudit()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var user = new TestUserInfoService();
        var createdAt = new DateTimeOffset(2026, 9, 5, 1, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(createdAt);
        var auditUser = new AuditUserIdProvider();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(
                new EntityCreatorInterceptor(auditUser, clock),
                new EntityModifierInterceptor(auditUser, clock)
            )
            .Options;
        await using var context = new AgwDbContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var guard = new ModelProviderUsageGuard(
            new TestAgentReferenceFacade(new EfRepository<Agent>(context), new EfRepository<Agentflow>(context))
        );
        var providers = new ProviderAppService(context, guard, user);
        var models = new ModelAppService(context, user);
        var relations = new ModelProviderAppService(context, guard, user);

        // Act
        var provider = await providers.CreateAsync(
            new ProviderCreateRequest(
                "provider",
                ProviderType.OpenAIResponses,
                null,
                "https://example.test",
                [new ProviderAuthConfigRequest(ProviderAuthType.ApiKey, "key-a", null)]
            ),
            "tester"
        );
        context.ChangeTracker.Clear();
        var model = await models.CreateAsync(new ModelCreateRequest("model", null, 8192, 2048), "tester");
        context.ChangeTracker.Clear();
        var relation = await relations.CreateAsync(
            new ModelProviderCreateRequest(model.Id, provider.Id, 0, 0, 0, 0, 0),
            "tester"
        );
        context.ChangeTracker.Clear();
        var credential = await context.ProviderAuthConfigs.SingleAsync(cancellationToken);

        // Assert
        foreach (var entity in new Agw.Shared.Data.BaseEntity[] { provider, model, relation, credential })
        {
            Assert.Equal("tester", entity.CreateBy);
            Assert.Equal(createdAt, entity.CreateTime);
        }

        // Act / Assert: both updates must advance audit metadata, including an already audited row.
        for (var update = 1; update <= 2; update++)
        {
            context.ChangeTracker.Clear();
            var updatedAt = createdAt.AddMinutes(update);
            clock.SetUtcNow(updatedAt);
            await providers.UpdateAsync(
                provider.Id,
                new ProviderUpdateRequest(
                    "provider",
                    ProviderType.OpenAIResponses,
                    "updated",
                    "https://example.test",
                    [new ProviderAuthConfigRequest(ProviderAuthType.ApiKey, $"key-{update}", null)]
                ),
                "tester"
            );
            context.ChangeTracker.Clear();
            await models.UpdateAsync(model.Id, new ModelUpdateRequest("model", "updated", 8192, 2048), "tester");
            context.ChangeTracker.Clear();
            await relations.UpdateAsync(relation.Id, new ModelProviderUpdateRequest(1, 1, 0, 0, 1), "tester");
            context.ChangeTracker.Clear();

            foreach (
                var entity in new Agw.Shared.Data.BaseEntity[]
                {
                    await context.Providers.SingleAsync(cancellationToken),
                    await context.Models.SingleAsync(cancellationToken),
                    await context.ModelProviders.SingleAsync(cancellationToken),
                }
            )
            {
                Assert.Equal("tester", entity.CreateBy);
                Assert.Equal(createdAt, entity.CreateTime);
                Assert.Equal("tester", entity.UpdateBy);
                Assert.Equal(updatedAt, entity.UpdateTime);
            }
            var persistedCredential = await context.ProviderAuthConfigs.SingleAsync(cancellationToken);
            Assert.Equal(provider.Id, persistedCredential.ProviderId);
            Assert.Equal($"key-{update}", persistedCredential.ApiKey);
            Assert.Equal("tester", persistedCredential.CreateBy);
            Assert.Equal(updatedAt, persistedCredential.CreateTime);
        }
    }

    private sealed class AuditUserIdProvider : IEntityAuditUserIdProvider
    {
        public string GetUserId() => UserInfoUtil.RequiredUserId;
    }
}
