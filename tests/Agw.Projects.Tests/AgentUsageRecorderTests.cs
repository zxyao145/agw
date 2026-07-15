using Agw.Infrastructure.Data;
using Agw.Projects.Infrastructure;
using Agw.Shared.Contracts.Projects;
using Agw.Testing;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Projects.Tests;

public class AgentUsageRecorderTests
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 7, 14, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task AddAsync_PersistsUsageFactWithDimensionsAndRecordedTime()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(Path.GetTempPath(), $"agw-usage-{Guid.NewGuid():N}.db");

        try
        {
            var options = CreateOptions(databasePath);
            await EnsureCreatedAsync(options, cancellationToken);
            await using var serviceProvider = CreateServiceProvider(options);
            var recorder = new AgentUsageRecorder(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                new TestTimeProvider(RecordedAt));
            var projectId = Guid.NewGuid();
            var contextId = Guid.NewGuid();

            await recorder.AddAsync(
                projectId,
                $"  {contextId:D}  ",
                "planner",
                new ProjectContextUsage
                {
                    InputTokenCount = 10,
                    OutputTokenCount = 20,
                    TotalTokenCount = 30,
                    CachedInputTokenCount = 4,
                    ReasoningTokenCount = 5
                },
                cancellationToken);

            await using var verifyContext = new AgwDbContext(options);
            var usage = await verifyContext.AgentUsages.SingleAsync(cancellationToken);
            Assert.NotEqual(Guid.Empty, usage.Id);
            Assert.Equal(projectId, usage.ProjectId);
            Assert.Equal(contextId.ToString("D"), usage.ContextId);
            Assert.Equal("planner", usage.AgentName);
            Assert.Equal(RecordedAt, usage.RecordedAt);
            Assert.Equal(10, usage.InputTokenCount);
            Assert.Equal(20, usage.OutputTokenCount);
            Assert.Equal(30, usage.TotalTokenCount);
            Assert.Equal(4, usage.CachedInputTokenCount);
            Assert.Equal(5, usage.ReasoningTokenCount);
            Assert.Empty(await verifyContext.ProjectContexts.ToListAsync(cancellationToken));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task AddAsync_SequentialCalls_AppendsOneFactPerCall()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(Path.GetTempPath(), $"agw-usage-{Guid.NewGuid():N}.db");

        try
        {
            var options = CreateOptions(databasePath);
            await EnsureCreatedAsync(options, cancellationToken);
            await using var serviceProvider = CreateServiceProvider(options);
            var recorder = new AgentUsageRecorder(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                new TestTimeProvider(RecordedAt));
            var projectId = Guid.NewGuid();

            await recorder.AddAsync(
                projectId,
                "context-1",
                "agent-1",
                new ProjectContextUsage { TotalTokenCount = 3 },
                cancellationToken);
            await recorder.AddAsync(
                projectId,
                "context-1",
                "agent-1",
                new ProjectContextUsage { TotalTokenCount = 7 },
                cancellationToken);

            await using var verifyContext = new AgwDbContext(options);
            var usages = await verifyContext.AgentUsages.ToListAsync(cancellationToken);
            Assert.Equal(2, usages.Count);
            Assert.Equal(10, usages.Sum(usage => usage.TotalTokenCount));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task AddAsync_ConcurrentCalls_AppendsEveryFact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(Path.GetTempPath(), $"agw-usage-{Guid.NewGuid():N}.db");

        try
        {
            var options = CreateOptions(databasePath);
            await EnsureCreatedAsync(options, cancellationToken);
            await using var serviceProvider = CreateServiceProvider(options);
            var recorder = new AgentUsageRecorder(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                new TestTimeProvider(RecordedAt));
            var projectId = Guid.NewGuid();
            var usage = new ProjectContextUsage { TotalTokenCount = 3 };

            await Task.WhenAll(
                recorder.AddAsync(projectId, "context-1", "agent-1", usage, cancellationToken),
                recorder.AddAsync(projectId, "context-1", "agent-1", usage, cancellationToken));

            await using var verifyContext = new AgwDbContext(options);
            var usages = await verifyContext.AgentUsages.ToListAsync(cancellationToken);
            Assert.Equal(2, usages.Count);
            Assert.Equal(6, usages.Sum(item => item.TotalTokenCount));
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

    private static async Task EnsureCreatedAsync(
        DbContextOptions<AgwDbContext> options,
        CancellationToken cancellationToken)
    {
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
    }
}
