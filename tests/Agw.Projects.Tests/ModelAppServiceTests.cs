using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Providers.Application;
using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Services;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;
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

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(128_000, 0)]
    [InlineData(128_000, -1)]
    [InlineData(128_000, 128_000)]
    [InlineData(128_000, 256_000)]
    public void ValidateTokenLimits_InvalidValues_ThrowsInvalidParam(int maxContextWindowTokens, int maxOutputTokens)
    {
        var service = new ModelDomainService(TimeProvider.System);

        var exception = Assert.Throws<AgwException>(() =>
            service.ValidateTokenLimits(maxContextWindowTokens, maxOutputTokens)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    private static ModelAppService CreateService(AgwDbContext dbContext) =>
        new(
            new EfRepository<AgwAiModel>(dbContext),
            dbContext,
            new ModelDomainService(TimeProvider.System),
            new TestUserInfoService()
        );
}
