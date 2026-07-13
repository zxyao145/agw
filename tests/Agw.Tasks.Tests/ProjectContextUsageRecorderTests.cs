using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Tasks.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tasks.Tests;

public class ProjectContextUsageRecorderTests
{
    [Fact]
    public async Task AddAsync_ExistingContext_AccumulatesEveryUsageCount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(Path.GetTempPath(), $"agw-usage-{Guid.NewGuid():N}.db");

        try
        {
            var options = CreateOptions(databasePath);
            var projectId = Guid.NewGuid();
            await SeedContextAsync(options, projectId, "context-1", cancellationToken);
            await using var serviceProvider = CreateServiceProvider(options);
            var recorder = new ProjectContextUsageRecorder(
                serviceProvider.GetRequiredService<IServiceScopeFactory>());

            await recorder.AddAsync(
                projectId,
                "context-1",
                new ProjectContextUsage
                {
                    InputTokenCount = 10,
                    OutputTokenCount = 20,
                    TotalTokenCount = 30,
                    CachedInputTokenCount = 4,
                    ReasoningTokenCount = 5
                },
                cancellationToken);
            await recorder.AddAsync(
                projectId,
                "context-1",
                new ProjectContextUsage
                {
                    InputTokenCount = 1,
                    OutputTokenCount = 2,
                    TotalTokenCount = 3,
                    CachedInputTokenCount = 6,
                    ReasoningTokenCount = 7
                },
                cancellationToken);

            await using var verifyContext = new AgwDbContext(options);
            var context = await verifyContext.ProjectContexts.SingleAsync(cancellationToken);
            Assert.Equal(11, context.Usage.InputTokenCount);
            Assert.Equal(22, context.Usage.OutputTokenCount);
            Assert.Equal(33, context.Usage.TotalTokenCount);
            Assert.Equal(10, context.Usage.CachedInputTokenCount);
            Assert.Equal(12, context.Usage.ReasoningTokenCount);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task AddAsync_ConcurrentCalls_DoesNotLoseUpdates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(Path.GetTempPath(), $"agw-usage-{Guid.NewGuid():N}.db");

        try
        {
            var options = CreateOptions(databasePath);
            var projectId = Guid.NewGuid();
            await SeedContextAsync(options, projectId, "context-1", cancellationToken);
            await using var serviceProvider = CreateServiceProvider(options);
            var recorder = new ProjectContextUsageRecorder(
                serviceProvider.GetRequiredService<IServiceScopeFactory>());
            var increment = new ProjectContextUsage
            {
                InputTokenCount = 1,
                OutputTokenCount = 2,
                TotalTokenCount = 3,
                CachedInputTokenCount = 4,
                ReasoningTokenCount = 5
            };

            await Task.WhenAll(
                recorder.AddAsync(projectId, "context-1", increment, cancellationToken),
                recorder.AddAsync(projectId, "context-1", increment, cancellationToken));

            await using var verifyContext = new AgwDbContext(options);
            var context = await verifyContext.ProjectContexts.SingleAsync(cancellationToken);
            Assert.Equal(2, context.Usage.InputTokenCount);
            Assert.Equal(4, context.Usage.OutputTokenCount);
            Assert.Equal(6, context.Usage.TotalTokenCount);
            Assert.Equal(8, context.Usage.CachedInputTokenCount);
            Assert.Equal(10, context.Usage.ReasoningTokenCount);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task AddAsync_ContextDoesNotExist_CompletesWithoutCreatingContext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(Path.GetTempPath(), $"agw-usage-{Guid.NewGuid():N}.db");

        try
        {
            var options = CreateOptions(databasePath);
            await using (var setupContext = new AgwDbContext(options))
            {
                await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            }

            await using var serviceProvider = CreateServiceProvider(options);
            var recorder = new ProjectContextUsageRecorder(
                serviceProvider.GetRequiredService<IServiceScopeFactory>());

            await recorder.AddAsync(
                Guid.NewGuid(),
                "missing-context",
                new ProjectContextUsage { TotalTokenCount = 3 },
                cancellationToken);

            await using var verifyContext = new AgwDbContext(options);
            Assert.Empty(await verifyContext.ProjectContexts.ToListAsync(cancellationToken));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(string databasePath) =>
        new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .UseSnakeCaseNamingConvention()
            .Options;

    private static ServiceProvider CreateServiceProvider(DbContextOptions<AgwDbContext> options)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        return services.BuildServiceProvider();
    }

    private static async Task SeedContextAsync(
        DbContextOptions<AgwDbContext> options,
        Guid projectId,
        string contextId,
        CancellationToken cancellationToken)
    {
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        dbContext.Projects.Add(new Project
        {
            Id = projectId,
            Name = $"Project-{projectId:N}",
            Type = ProjectType.UserDefined,
            Enable = true,
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow()
        });
        dbContext.ProjectContexts.Add(new ProjectContext
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ContextId = contextId,
            Title = "Context",
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow()
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
