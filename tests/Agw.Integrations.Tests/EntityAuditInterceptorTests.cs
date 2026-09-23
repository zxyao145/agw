using System.Security.Claims;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Providers.Contracts;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Tests;

public class EntityAuditInterceptorTests
{
    [Fact]
    public async Task SaveChanges_ModifiedEntityThroughAgwDbContext_PersistsAuditFields()
    {
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-1")], authenticationType: "Test")
            )
        );
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

    [Fact]
    public async Task SaveChanges_AddedEntityWithoutUserId_FillsCurrentUser()
    {
        using var userScope = PushUser("user-1");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateOwnedContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var entity = new OwnedEntity { Name = "owned" };
        context.Entities.Add(entity);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("user-1", entity.UserId);
    }

    [Fact]
    public async Task SaveChanges_ModifiedEntityWithoutUserId_Throws()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateOwnedContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        // 无用户上下文时写入的行保留空 UserId，供修改阶段校验。
        // A row written without a user context keeps an empty UserId for the modification stage to validate.
        var entity = new OwnedEntity { Name = "owned" };
        context.Entities.Add(entity);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Null(entity.UserId);

        using var userScope = PushUser("user-1");
        entity.Name = "updated";

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.SaveChangesAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal("UserId is required.", exception.Message);
    }

    [Fact]
    public async Task SaveChanges_AddedEntityOwnedByAnotherUser_Throws()
    {
        using var userScope = PushUser("user-1");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateOwnedContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        context.Entities.Add(new OwnedEntity { Name = "owned", CreateBy = "user-2" });

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.SaveChangesAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal("CreateBy must match the current user.", exception.Message);
    }

    [Fact]
    public async Task SaveChanges_JobLogOwnedByAnotherUser_IsAllowedOnCreateAndRejectedOnUpdate()
    {
        using var userScope = PushUser("user-1");
        // 生产迁移不生成外键，测试连接同样关闭外键强制。
        // Production migrations emit no foreign keys, so the test connection disables their enforcement too.
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateOwnedContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        // JobLog 由调度上下文代写，新建放行；同一条规则在修改阶段不放行。
        // JobLog rows are written on behalf of the scheduler, so creation passes while modification does not.
        var log = new JobLog
        {
            Id = Guid.CreateVersion7(),
            JobId = Guid.CreateVersion7(),
            TaskId = Guid.CreateVersion7(),
            StartTime = DateTimeOffset.UnixEpoch,
            CreateBy = "user-2",
        };
        context.JobLogs.Add(log);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal("user-2", log.CreateBy);

        log.Success = true;

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.SaveChangesAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal("CreateBy must match the current user.", exception.Message);
    }

    private static IDisposable PushUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], authenticationType: "Test")
            )
        );

    private static OwnedDbContext CreateOwnedContext(SqliteConnection connection)
    {
        var userIdProvider = new TestUserIdProvider("user-1");
        var options = new DbContextOptionsBuilder<OwnedDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(
                new EntityCreatorInterceptor(userIdProvider, TimeProvider.System),
                new EntityModifierInterceptor(userIdProvider, TimeProvider.System),
                new EntitySoftDeleteInterceptor(userIdProvider, TimeProvider.System)
            )
            .Options;
        return new OwnedDbContext(options);
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

    private sealed class OwnedDbContext : DbContext
    {
        public OwnedDbContext(DbContextOptions<OwnedDbContext> options)
            : base(options) { }

        public DbSet<OwnedEntity> Entities => Set<OwnedEntity>();

        public DbSet<JobLog> JobLogs => Set<JobLog>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<OwnedEntity>();
            modelBuilder.Entity<JobLog>().Ignore(entity => entity.Job);
        }
    }

    private sealed class OwnedEntity : IEntityCreator, IEntityModifier
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? UserId { get; set; }
        public DateTimeOffset CreateTime { get; set; }
        public string? CreateBy { get; set; }
        public DateTimeOffset? UpdateTime { get; set; }
        public string? UpdateBy { get; set; }
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
