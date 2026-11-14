using System.Security.Claims;
using Agw.Agents.Contracts.Catalog;
using Agw.Auth.Contracts;
using Agw.Events.Contracts;
using Agw.Host.Controllers;
using Agw.Infrastructure.Data;
using Agw.Jobs.Contracts.Metrics;
using Agw.Projects.Contracts.Metrics;
using Agw.Shared.Data.Entities.Projects;
using Agw.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Host.Tests;

public class DashboardControllerTests : IDisposable
{
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], authenticationType: "Test")
        )
    );

    public void Dispose() => _userScope.Dispose();

    [Fact]
    public async Task GetStats_SharedScopedDependencies_DoesNotOverlapCalls()
    {
        var tracker = new NonConcurrentCallTracker();
        var controller = new DashboardController(
            new TrackingJobMetricsFacade(tracker),
            new TrackingProjectMetricsFacade(tracker),
            new TrackingAgentCatalogFacade(tracker)
        );

        var result = await controller.GetStats(TestContext.Current.CancellationToken);

        Assert.IsType<DashboardStatsResponse>(ReadStats(result));
        Assert.Equal(3, tracker.CallCount);
    }

    [Fact]
    public async Task GetStats_MultipleAgentUsages_SumsAllTokenUsage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var projectId = Guid.CreateVersion7();
        dbContext.AgentUsages.AddRange(
            CreateAgentUsage(projectId, "context-1", 10, 20, 30),
            CreateAgentUsage(projectId, "context-2", 5, 7, 12)
        );
        await dbContext.SaveChangesAsync(cancellationToken);
        var controller = CreateController(scope.ServiceProvider);

        var result = await controller.GetStats(cancellationToken);

        var stats = ReadStats(result);
        Assert.Equal(15L, ReadLongProperty(stats, "UsageInputTokenCount"));
        Assert.Equal(27L, ReadLongProperty(stats, "UsageOutputTokenCount"));
        Assert.Equal(42L, ReadLongProperty(stats, "UsageTotalTokenCount"));
    }

    [Fact]
    public async Task GetStats_NoAgentUsages_ReturnsZeroTokenUsage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var controller = CreateController(scope.ServiceProvider);

        var result = await controller.GetStats(cancellationToken);

        var stats = ReadStats(result);
        Assert.Equal(0L, ReadLongProperty(stats, "UsageInputTokenCount"));
        Assert.Equal(0L, ReadLongProperty(stats, "UsageOutputTokenCount"));
        Assert.Equal(0L, ReadLongProperty(stats, "UsageTotalTokenCount"));
    }

    [Fact]
    public async Task GetStats_ProjectDeleted_IncludesRetainedUsage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var projectId = Guid.CreateVersion7();
        dbContext.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = "Deleted project",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow(),
            }
        );
        dbContext.ProjectConversations.Add(
            new ProjectConversation
            {
                Id = Guid.CreateVersion7(),
                ProjectId = projectId,
                ContextId = "context-1",
                Title = "Context",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow(),
            }
        );
        dbContext.AgentUsages.Add(CreateAgentUsage(projectId, "context-1", 10, 20, 30));
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.Projects.Remove(await dbContext.Projects.SingleAsync(cancellationToken));
        await dbContext.SaveChangesAsync(cancellationToken);

        Assert.Empty(await dbContext.ProjectConversations.ToListAsync(cancellationToken));
        Assert.Single(await dbContext.AgentUsages.ToListAsync(cancellationToken));
        var stats = ReadStats(await CreateController(scope.ServiceProvider).GetStats(cancellationToken));
        Assert.Equal(30L, ReadLongProperty(stats, "UsageTotalTokenCount"));
    }

    [Fact]
    public async Task GetStats_UserHasJobs_ReturnsOwnJobCount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var eventJobs = services.GetRequiredService<IEventJobFacade>();
        var projectId = await CreateJobProjectAsync(services, cancellationToken);
        await eventJobs.CreateAsync(CreateJobDefinition("Current user job", projectId), cancellationToken);
        var deletedJob = await eventJobs.CreateAsync(CreateJobDefinition("Deleted job", projectId), cancellationToken);
        await eventJobs.DeleteAsync(deletedJob.Id, cancellationToken);

        using (
            UserInfoUtil.Push(
                new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "other")], authenticationType: "Test")
                )
            )
        )
        {
            await using var otherScope = host.Services.CreateAsyncScope();
            var otherServices = otherScope.ServiceProvider;
            var otherProjectId = await CreateJobProjectAsync(otherServices, cancellationToken);
            await otherServices
                .GetRequiredService<IEventJobFacade>()
                .CreateAsync(CreateJobDefinition("Other user job", otherProjectId), cancellationToken);
        }

        var stats = ReadStats(await CreateController(scope.ServiceProvider).GetStats(cancellationToken));

        Assert.Equal(1, stats.JobCount);
    }

    private static DashboardController CreateController(IServiceProvider services) =>
        ActivatorUtilities.CreateInstance<DashboardController>(services);

    private static async Task<Guid> CreateJobProjectAsync(IServiceProvider services, CancellationToken token)
    {
        var db = services.GetRequiredService<AgwDbContext>();
        var project = new Project { Id = Guid.CreateVersion7(), Name = "Dashboard jobs project" };
        db.Projects.Add(project);
        await db.SaveChangesAsync(token);
        return project.Id;
    }

    private static EventJobDefinition CreateJobDefinition(string name, Guid projectId) =>
        new()
        {
            Name = name,
            Enabled = true,
            ProjectId = projectId,
            TargetKind = EventJobTargetKind.Agent,
            AgentId = Guid.CreateVersion7(),
            Instructions = "Run",
            Schedule = new EventJobSchedule { Kind = "Interval", Interval = "PT1M" },
        };

    private static AgentUsage CreateAgentUsage(
        Guid projectId,
        string contextId,
        long inputTokenCount,
        long outputTokenCount,
        long totalTokenCount
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            ContextId = contextId,
            AgentName = "planner",
            UserId = "tester",
            RecordedAt = TimeProvider.System.GetUtcNow(),
            InputTokenCount = inputTokenCount,
            OutputTokenCount = outputTokenCount,
            TotalTokenCount = totalTokenCount,
        };

    private static DashboardStatsResponse ReadStats(IActionResult result)
    {
        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
        var dataProperty = result.GetType().GetProperty("Data");
        Assert.NotNull(dataProperty);
        return Assert.IsType<DashboardStatsResponse>(dataProperty!.GetValue(result));
    }

    private static long ReadLongProperty(DashboardStatsResponse stats, string propertyName)
    {
        var property = typeof(DashboardStatsResponse).GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<long>(property!.GetValue(stats));
    }

    private sealed class NonConcurrentCallTracker
    {
        private int _activeCalls;
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<T> RunAsync<T>(T result)
        {
            if (Interlocked.CompareExchange(ref _activeCalls, 1, 0) != 0)
            {
                throw new InvalidOperationException("Dashboard metric calls overlapped on a shared request scope.");
            }

            try
            {
                Interlocked.Increment(ref _callCount);
                await Task.Yield();
                return result;
            }
            finally
            {
                Volatile.Write(ref _activeCalls, 0);
            }
        }
    }

    private sealed class TrackingJobMetricsFacade : IJobMetricsFacade
    {
        private readonly NonConcurrentCallTracker _tracker;

        public TrackingJobMetricsFacade(NonConcurrentCallTracker tracker)
        {
            _tracker = tracker;
        }

        public Task<JobMetrics> GetAsync(CancellationToken cancellationToken = default) =>
            _tracker.RunAsync(new JobMetrics(1));
    }

    private sealed class TrackingProjectMetricsFacade : IProjectMetricsFacade
    {
        private readonly NonConcurrentCallTracker _tracker;

        public TrackingProjectMetricsFacade(NonConcurrentCallTracker tracker)
        {
            _tracker = tracker;
        }

        public Task<ProjectMetrics> GetAsync(CancellationToken cancellationToken = default) =>
            _tracker.RunAsync(new ProjectMetrics(2, 3, 4, 5, 6, 11));
    }

    private sealed class TrackingAgentCatalogFacade : IAgentCatalogFacade
    {
        private readonly NonConcurrentCallTracker _tracker;

        public TrackingAgentCatalogFacade(NonConcurrentCallTracker tracker)
        {
            _tracker = tracker;
        }

        public Task<AgentCatalogMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) =>
            _tracker.RunAsync(new AgentCatalogMetrics(7, 8));

        public Task<bool> IsOwnedTargetAsync(
            Agw.Agents.Contracts.Execution.AgentRuntimeType type,
            Guid id,
            string ownerUserId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(true);

        public Task<IReadOnlyList<AgentDescriptor>> ListDiscoverableAsync(
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<AgentDescriptor?> FindDiscoverableByNameAsync(
            string name,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlySet<Guid>> FilterExistingMcpServerIdsAsync(
            IReadOnlyCollection<Guid> serverIds,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
