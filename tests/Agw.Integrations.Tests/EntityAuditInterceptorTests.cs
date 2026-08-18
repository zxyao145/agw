using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Tests;

public class EntityAuditInterceptorTests
{
    [Fact]
    public async Task SaveChanges_ModifiedEntityThroughAgwDbContext_PersistsAuditFields()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var userIdProvider = new TestUserIdProvider("user-1");
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(
                new EntityCreatorInterceptor(userIdProvider, TimeProvider.System),
                new EntityModifierInterceptor(userIdProvider, TimeProvider.System),
                new EntitySoftDeleteInterceptor(userIdProvider, TimeProvider.System)
            )
            .Options;

        await using var context = new AgwDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var provider = new Provider
        {
            Name = "provider",
            ProviderType = ProviderType.OpenAIResponses,
            Endpoint = "https://example.test",
        };
        context.Providers.Add(provider);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        provider.Description = "updated";
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        var persistedProvider = await context
            .Providers.AsNoTracking()
            .SingleAsync(item => item.Id == provider.Id, TestContext.Current.CancellationToken);

        Assert.Equal("user-1", persistedProvider.UpdateBy);
        Assert.NotNull(persistedProvider.UpdateTime);
    }

    [Fact]
    public async Task SaveChanges_AddedModifiedAndDeletedEntity_StampsAuditFields()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var userIdProvider = new TestUserIdProvider("user-1");
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(
                new EntityCreatorInterceptor(userIdProvider, TimeProvider.System),
                new EntityModifierInterceptor(userIdProvider, TimeProvider.System),
                new EntitySoftDeleteInterceptor(userIdProvider, TimeProvider.System)
            )
            .Options;

        await using var context = new AuditDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var entity = new AuditEntity { Name = "initial" };
        context.Entities.Add(entity);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("user-1", entity.CreateBy);
        Assert.NotEqual(default, entity.CreateTime);

        entity.Name = "updated";
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("user-1", entity.UpdateBy);
        Assert.NotNull(entity.UpdateTime);

        var explicitCreateTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var explicitUpdateTime = explicitCreateTime.AddMinutes(1);
        var systemEntity = new AuditEntity
        {
            Name = "system",
            CreateBy = "a2a",
            CreateTime = explicitCreateTime,
        };
        context.Entities.Add(systemEntity);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        systemEntity.Name = "system-updated";
        systemEntity.UpdateBy = "a2a";
        systemEntity.UpdateTime = explicitUpdateTime;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("a2a", systemEntity.CreateBy);
        Assert.Equal(explicitCreateTime, systemEntity.CreateTime);
        Assert.Equal("a2a", systemEntity.UpdateBy);
        Assert.Equal(explicitUpdateTime, systemEntity.UpdateTime);

        context.Entities.Remove(entity);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var visibleEntities = await context.Entities.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(visibleEntities, item => item.Id == entity.Id);
        Assert.Contains(visibleEntities, item => item.Id == systemEntity.Id);

        var deleted = await context
            .Entities.IgnoreQueryFilters([SoftDeleteQueryFilterNames.SoftDelete])
            .AsNoTracking()
            .SingleAsync(item => item.Id == entity.Id, TestContext.Current.CancellationToken);

        Assert.True(deleted.IsDeleted);
        Assert.Equal("user-1", deleted.DeleteBy);
        Assert.NotNull(deleted.DeletionTime);
    }

    private sealed class TestUserIdProvider : IEntityAuditUserIdProvider
    {
        private readonly string _userId;

        public TestUserIdProvider(string userId)
        {
            _userId = userId;
        }

        public string GetUserId() => _userId;
    }

    private sealed class AuditDbContext : DbContext
    {
        public AuditDbContext(DbContextOptions<AuditDbContext> options)
            : base(options) { }

        public DbSet<AuditEntity> Entities => Set<AuditEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<AuditEntity>();
            modelBuilder.ApplySoftDeleteQueryFilters();
        }
    }

    private sealed class AuditEntity : IEntityCreator, IEntityModifier, ISoftDeleteAudit
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset CreateTime { get; set; }
        public string? CreateBy { get; set; }
        public DateTimeOffset? UpdateTime { get; set; }
        public string? UpdateBy { get; set; }
        public bool IsDeleted { get; set; }
        public DateTimeOffset? DeletionTime { get; set; }
        public string? DeleteBy { get; set; }
    }
}
