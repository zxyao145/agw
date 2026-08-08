using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Tools.ToolBlocks.Storage;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Tests;

public sealed class EfProjectMemoryStoreTests
{
    [Fact]
    public async Task FileOperations_SameProjectShareAcrossStoreInstances()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var database = new TestDbContext(options))
        {
            await database.Database.EnsureCreatedAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new TestDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var projectId = Guid.CreateVersion7();
        var firstStore = new EfProjectMemoryStore(
            scopeFactory,
            TimeProvider.System,
            projectId);
        var secondStore = new EfProjectMemoryStore(
            scopeFactory,
            TimeProvider.System,
            projectId);

        await firstStore.WriteAsync("notes.md", "shared content", cancellationToken);

        Assert.Equal(
            "shared content",
            await secondStore.ReadAsync("notes.md", cancellationToken));
        await using var verification = new TestDbContext(options);
        var entry = await verification.ProjectMemories
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        Assert.Equal(projectId, entry.ProjectId);
        Assert.Equal("shared content", entry.Content);
    }

    [Fact]
    public async Task FileOperations_DifferentProjectsRemainIsolated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var database = new TestDbContext(options))
        {
            await database.Database.EnsureCreatedAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new TestDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var firstStore = new EfProjectMemoryStore(
            scopeFactory,
            TimeProvider.System,
            Guid.CreateVersion7());
        var secondStore = new EfProjectMemoryStore(
            scopeFactory,
            TimeProvider.System,
            Guid.CreateVersion7());

        await firstStore.WriteAsync("notes.md", "first project", cancellationToken);
        await secondStore.WriteAsync("notes.md", "second project", cancellationToken);

        Assert.Equal("first project", await firstStore.ReadAsync("notes.md", cancellationToken));
        Assert.Equal("second project", await secondStore.ReadAsync("notes.md", cancellationToken));
    }

    [Fact]
    public async Task WriteAsync_ConcurrentSamePath_UpsertsSingleEntry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var database = new TestDbContext(options))
        {
            await database.Database.EnsureCreatedAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new TestDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var projectId = Guid.CreateVersion7();
        var store = new EfProjectMemoryStore(
            scopeFactory,
            TimeProvider.System,
            new InMemoryApplicationLock(),
            projectId);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = Enumerable.Range(0, 12)
            .Select(async index =>
            {
                await start.Task;
                await store.WriteAsync("notes.md", $"content-{index}", cancellationToken);
            })
            .ToArray();

        start.SetResult();
        await Task.WhenAll(writes);

        await using var verification = new TestDbContext(options);
        var entry = await verification.ProjectMemories.SingleAsync(cancellationToken);
        Assert.StartsWith("content-", entry.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilePaths_AncestorCollision_IsRejectedAcrossStoreInstances()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var database = new TestDbContext(options))
        {
            await database.Database.EnsureCreatedAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new TestDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var applicationLock = new InMemoryApplicationLock();
        var projectId = Guid.CreateVersion7();
        var fileFirstStore = new EfProjectMemoryStore(
            scopeFactory,
            TimeProvider.System,
            applicationLock,
            projectId);
        await fileFirstStore.WriteAsync("foo", "file", cancellationToken);

        var descendantException = await Assert.ThrowsAsync<AgwException>(() =>
            fileFirstStore.WriteAsync("foo/bar.txt", "child", cancellationToken));
        Assert.Equal(ErrorCodes.InvalidParam.Code, descendantException.Code);

        var directoryFirstStore = new EfProjectMemoryStore(
            scopeFactory,
            TimeProvider.System,
            applicationLock,
            Guid.CreateVersion7());
        await directoryFirstStore.WriteAsync("foo/bar.txt", "child", cancellationToken);
        var ancestorException = await Assert.ThrowsAsync<AgwException>(() =>
            directoryFirstStore.WriteAsync("foo", "file", cancellationToken));
        Assert.Equal(ErrorCodes.InvalidParam.Code, ancestorException.Code);
    }

    private sealed class TestDbContext : DbContext
    {
        public TestDbContext(DbContextOptions<TestDbContext> options)
            : base(options)
        {
        }

        public DbSet<ProjectMemoryEntry> ProjectMemories => Set<ProjectMemoryEntry>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<ProjectMemoryEntry>();
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Path).IsRequired();
            entity.Property(entry => entry.Content).IsRequired();
            entity.HasIndex(entry => new
            {
                entry.ProjectId,
                entry.Path
            }).IsUnique();
        }
    }
}
