using System.Security.Claims;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Tools;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Tools.Application.Persistence;
using Agw.Tools.ToolBlocks.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Tests;

public sealed class EfProjectMemoryStoreTests : IDisposable
{
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], authenticationType: "Test")
        )
    );

    public void Dispose() => _userScope.Dispose();

    [Fact]
    public async Task FileOperations_SameProjectShareAcrossStoreInstances()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
        await using (var database = new AgwDbContext(options))
        {
            await database.Database.EnsureCreatedAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<AgwDbContext>(_ => new AgwDbContext(options));
        services.AddScoped<IProjectMemoryPersistence, ProjectMemoryPersistence>();
        await using var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var projectId = Guid.CreateVersion7();
        await SeedProjectsAsync(options, cancellationToken, projectId);
        var firstStore = new EfProjectMemoryStore(scopeFactory, TimeProvider.System, projectId);
        var secondStore = new EfProjectMemoryStore(scopeFactory, TimeProvider.System, projectId);

        await firstStore.WriteAsync("notes.md", "shared content", cancellationToken);

        Assert.Equal("shared content", await secondStore.ReadAsync("notes.md", cancellationToken));
        await using var verification = new AgwDbContext(options);
        var entry = await verification.ProjectMemories.AsNoTracking().SingleAsync(cancellationToken);
        Assert.Equal(projectId, entry.ProjectId);
        Assert.Equal("shared content", entry.Content);
    }

    [Fact]
    public async Task FileOperations_DifferentProjectsRemainIsolated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
        await using (var database = new AgwDbContext(options))
        {
            await database.Database.EnsureCreatedAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<AgwDbContext>(_ => new AgwDbContext(options));
        services.AddScoped<IProjectMemoryPersistence, ProjectMemoryPersistence>();
        await using var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var firstProjectId = Guid.CreateVersion7();
        var secondProjectId = Guid.CreateVersion7();
        await SeedProjectsAsync(options, cancellationToken, firstProjectId, secondProjectId);
        var firstStore = new EfProjectMemoryStore(scopeFactory, TimeProvider.System, firstProjectId);
        var secondStore = new EfProjectMemoryStore(scopeFactory, TimeProvider.System, secondProjectId);

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
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
        await using (var database = new AgwDbContext(options))
        {
            await database.Database.EnsureCreatedAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<AgwDbContext>(_ => new AgwDbContext(options));
        services.AddScoped<IProjectMemoryPersistence, ProjectMemoryPersistence>();
        await using var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var projectId = Guid.CreateVersion7();
        await SeedProjectsAsync(options, cancellationToken, projectId);
        var store = new EfProjectMemoryStore(
            scopeFactory,
            TimeProvider.System,
            new InMemoryApplicationLock(),
            projectId
        );
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = Enumerable
            .Range(0, 12)
            .Select(async index =>
            {
                await start.Task;
                await store.WriteAsync("notes.md", $"content-{index}", cancellationToken);
            })
            .ToArray();

        start.SetResult();
        await Task.WhenAll(writes);

        await using var verification = new AgwDbContext(options);
        var entry = await verification.ProjectMemories.SingleAsync(cancellationToken);
        Assert.StartsWith("content-", entry.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilePaths_AncestorCollision_IsRejectedAcrossStoreInstances()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
        await using (var database = new AgwDbContext(options))
        {
            await database.Database.EnsureCreatedAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<AgwDbContext>(_ => new AgwDbContext(options));
        services.AddScoped<IProjectMemoryPersistence, ProjectMemoryPersistence>();
        await using var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var applicationLock = new InMemoryApplicationLock();
        var projectId = Guid.CreateVersion7();
        var directoryProjectId = Guid.CreateVersion7();
        await SeedProjectsAsync(options, cancellationToken, projectId, directoryProjectId);
        var fileFirstStore = new EfProjectMemoryStore(scopeFactory, TimeProvider.System, applicationLock, projectId);
        await fileFirstStore.WriteAsync("foo", "file", cancellationToken);

        var descendantException = await Assert.ThrowsAsync<AgwException>(() =>
            fileFirstStore.WriteAsync("foo/bar.txt", "child", cancellationToken)
        );
        Assert.Equal(ErrorCodes.InvalidParam.Code, descendantException.Code);

        var directoryFirstStore = new EfProjectMemoryStore(
            scopeFactory,
            TimeProvider.System,
            applicationLock,
            directoryProjectId
        );
        await directoryFirstStore.WriteAsync("foo/bar.txt", "child", cancellationToken);
        var ancestorException = await Assert.ThrowsAsync<AgwException>(() =>
            directoryFirstStore.WriteAsync("foo", "file", cancellationToken)
        );
        Assert.Equal(ErrorCodes.InvalidParam.Code, ancestorException.Code);
    }

    [Fact]
    public async Task WriteAsync_ProjectOwnedByAnotherUser_ThrowsNotFoundWithoutCreatingMemory()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
        var projectId = Guid.CreateVersion7();
        await using (var database = new AgwDbContext(options))
        {
            await database.Database.EnsureCreatedAsync(cancellationToken);
            database.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = "foreign-project",
                    CreateBy = "foreign",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await database.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<AgwDbContext>(_ => new AgwDbContext(options));
        services.AddScoped<IProjectMemoryPersistence, ProjectMemoryPersistence>();
        await using var serviceProvider = services.BuildServiceProvider();
        var store = new EfProjectMemoryStore(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            projectId
        );

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            store.WriteAsync("notes.md", "foreign content", cancellationToken)
        );

        // Assert
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        await using var verification = new AgwDbContext(options);
        Assert.Empty(await verification.ProjectMemories.ToListAsync(cancellationToken));
    }

    private static async Task SeedProjectsAsync(
        DbContextOptions<AgwDbContext> options,
        CancellationToken cancellationToken,
        params Guid[] projectIds
    )
    {
        await using var context = new AgwDbContext(options);
        context.Projects.AddRange(
            projectIds.Select(projectId => new Project
            {
                Id = projectId,
                Name = $"project-{projectId:N}",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow(),
            })
        );
        await context.SaveChangesAsync(cancellationToken);
    }
}
