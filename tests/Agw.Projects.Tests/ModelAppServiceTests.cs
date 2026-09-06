using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Providers.Application;
using Agw.Providers.Contracts.Manager;
using Agw.Shared.Data.Entities.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public sealed class ModelAppServiceTests
{
    [Fact]
    public async Task CreateAsync_ValidTokenLimits_PersistsBothLimits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(
                new EntityCreatorInterceptor(new TestAuditUserIdProvider(), TimeProvider.System),
                new EntityModifierInterceptor(new TestAuditUserIdProvider(), TimeProvider.System)
            )
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        var service = CreateService(dbContext);

        var model = await service.CreateAsync(new ModelCreateRequest("test-model", null, 128_000, 16_000), "tester");

        Assert.Equal(128_000, model.MaxContextWindowTokens);
        Assert.Equal(16_000, model.MaxOutputTokens);
        var persisted = await dbContext.Models.SingleAsync(cancellationToken);
        Assert.Equal(128_000, persisted.MaxContextWindowTokens);
        Assert.Equal(16_000, persisted.MaxOutputTokens);
    }

    [Fact]
    public async Task UpdateAsync_ValidTokenLimits_PersistsBothLimits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(
                new EntityCreatorInterceptor(new TestAuditUserIdProvider(), TimeProvider.System),
                new EntityModifierInterceptor(new TestAuditUserIdProvider(), TimeProvider.System)
            )
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        var existing = new AgwAiModel
        {
            Name = "test-model",
            MaxContextWindowTokens = 128_000,
            MaxOutputTokens = 16_000,
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };
        dbContext.Models.Add(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
        var service = CreateService(dbContext);

        var model = await service.UpdateAsync(
            existing.Id,
            new ModelUpdateRequest("updated-model", null, 256_000, 64_000),
            "tester"
        );

        Assert.NotNull(model);
        Assert.Equal(256_000, model.MaxContextWindowTokens);
        Assert.Equal(64_000, model.MaxOutputTokens);
        dbContext.ChangeTracker.Clear();
        var persisted = await dbContext.Models.SingleAsync(cancellationToken);
        Assert.Equal(256_000, persisted.MaxContextWindowTokens);
        Assert.Equal(64_000, persisted.MaxOutputTokens);
    }

    private static ModelAppService CreateService(AgwDbContext dbContext) => new(dbContext, new TestUserInfoService());
}
