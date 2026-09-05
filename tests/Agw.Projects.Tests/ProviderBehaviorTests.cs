using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Providers.Domain.Behaviors;
using Agw.Shared.Data.Entities.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class ProviderBehaviorTests
{
    [Fact]
    public async Task ApplyAuthConfigs_TrackedChildren_ReconcilesByIdAndPreservesAudit()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        _ = new TestUserInfoService();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new EntityCreatorInterceptor(new TestAuditUserIdProvider(), TimeProvider.System))
            .Options;
        await using var context = new AgwDbContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var createdAt = new DateTimeOffset(2026, 9, 4, 1, 0, 0, TimeSpan.Zero);
        var kept = new ProviderAuthConfig
        {
            Id = Guid.CreateVersion7(),
            ApiKey = "before",
            CreateBy = "tester",
            CreateTime = createdAt,
        };
        var removed = new ProviderAuthConfig
        {
            Id = Guid.CreateVersion7(),
            ApiKey = "remove",
            CreateBy = "tester",
            CreateTime = createdAt,
        };
        var provider = new Provider
        {
            Id = Guid.CreateVersion7(),
            Name = "provider",
            CreateBy = "tester",
            AuthConfigs = [kept, removed],
        };
        context.Providers.Add(provider);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();
        provider = await context.Providers.Include(item => item.AuthConfigs).SingleAsync(cancellationToken);
        var collection = provider.AuthConfigs;
        var tracked = collection.Single(item => item.Id == kept.Id);
        var proposed = new[]
        {
            new ProviderAuthConfig
            {
                Id = tracked.Id,
                ProviderId = Guid.CreateVersion7(),
                ApiKey = "after",
                Enable = false,
            },
            new ProviderAuthConfig { ApiKey = "new" },
        };

        // Act
        new ProviderBehavior(provider).ApplyAuthConfigs(proposed);

        // Assert
        Assert.Same(collection, provider.AuthConfigs);
        Assert.Same(tracked, provider.AuthConfigs.Single(item => item.Id == kept.Id));
        Assert.Equal("tester", tracked.CreateBy);
        Assert.Equal(createdAt, tracked.CreateTime);
        Assert.Null(tracked.UpdateTime);
        Assert.Equal(2, collection.Count);
        Assert.DoesNotContain(collection, item => item.Id == removed.Id);
        Assert.All(collection, item => Assert.Equal(provider.Id, item.ProviderId));
        Assert.NotEqual(provider.Id, proposed[0].ProviderId);
        Assert.Null(proposed[1].Provider);

        await context.SaveChangesAsync(cancellationToken);
        var addedId = provider.AuthConfigs.Single(item => item.ApiKey == "new").Id;
        Assert.NotEqual(Guid.Empty, addedId);
        context.ChangeTracker.Clear();
        var persisted = await context.ProviderAuthConfigs.OrderBy(item => item.Id).ToListAsync(cancellationToken);
        Assert.Equal(2, persisted.Count);
        var updated = persisted.Single(item => item.Id == kept.Id);
        Assert.Equal("after", updated.ApiKey);
        Assert.False(updated.Enable);
        Assert.Equal(createdAt, updated.CreateTime);
        Assert.Contains(persisted, item => item.Id == addedId && item.ApiKey == "new");
        Assert.DoesNotContain(persisted, item => item.Id == removed.Id);
    }
}
