using Agw.Infrastructure.Data;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Events;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public sealed partial class ProjectDeletionCoordinatorTests
{
    [Fact]
    public async Task DeleteProjectAsync_EventSubscriptionExists_DisablesSubscriptionAndRetainsExecutionIdentity()
    {
        using var userScope = PushUser("tester");
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        var projectId = Guid.CreateVersion7();
        await SeedProjectGraphAsync(options, projectId, Guid.CreateVersion7(), "tester", "event-project");
        var subscriptionId = Guid.CreateVersion7();
        var otherSubscriptionId = Guid.CreateVersion7();
        var executionId = Guid.CreateVersion7();
        await using (var seed = new AgwDbContext(options))
        {
            seed.EventSubscriptions.AddRange(
                new EventSubscription
                {
                    Id = subscriptionId,
                    ProjectId = projectId,
                    Name = "Project events",
                    Enabled = true,
                    CreateBy = "tester",
                    Version = 3,
                },
                new EventSubscription
                {
                    Id = otherSubscriptionId,
                    ProjectId = Guid.CreateVersion7(),
                    Name = "Other project events",
                    Enabled = true,
                    CreateBy = "tester",
                    Version = 1,
                }
            );
            seed.BackgroundExecutions.Add(
                new BackgroundExecution
                {
                    Id = executionId,
                    ProjectId = projectId,
                    CallerKey = "ext:test",
                    CorrelationKey = "completed-attempt",
                    CreateBy = "tester",
                    State = AgentExecutionState.Completed,
                }
            );
            await seed.SaveChangesAsync(ct);
        }

        await using var db = new AgwDbContext(options);
        var coordinator = TestProjectPersistence.CreateDeletionCoordinator(db);
        Assert.True(await coordinator.DeleteProjectAsync(new ProjectDeletionTarget(projectId, "tester"), ct));

        await using var actual = new AgwDbContext(options);
        var subscription = await actual.EventSubscriptions.SingleAsync(row => row.Id == subscriptionId, ct);
        Assert.False(subscription.Enabled);
        Assert.Equal(4, subscription.Version);
        Assert.True((await actual.EventSubscriptions.SingleAsync(row => row.Id == otherSubscriptionId, ct)).Enabled);
        Assert.True(await actual.BackgroundExecutions.AnyAsync(row => row.Id == executionId, ct));
    }

    [Fact]
    public async Task DeleteProjectAsync_ProjectHasJobs_DeletesJobRecordsOfThatProjectOnly()
    {
        using var userScope = PushUser("tester");
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        var projectId = Guid.CreateVersion7();
        var otherProjectId = Guid.CreateVersion7();
        await SeedProjectGraphAsync(options, projectId, Guid.CreateVersion7(), "tester", "event-jobs");
        await SeedProjectGraphAsync(options, otherProjectId, Guid.CreateVersion7(), "tester", "other-event-jobs");
        var deletedJobId = Guid.CreateVersion7();
        var otherJobId = Guid.CreateVersion7();
        const int sourceVersionBefore = 3;
        await using (var seed = new AgwDbContext(options))
        {
            SeedEventJob(seed, projectId, deletedJobId, sourceVersionBefore);
            SeedEventJob(seed, otherProjectId, otherJobId, sourceVersionBefore);
            await seed.SaveChangesAsync(ct);
        }

        await using var db = new AgwDbContext(options);
        var coordinator = TestProjectPersistence.CreateDeletionCoordinator(db);
        Assert.True(await coordinator.DeleteProjectAsync(new ProjectDeletionTarget(projectId, "tester"), ct));

        var deletedJobKey = deletedJobId.ToString("D");
        var otherJobKey = otherJobId.ToString("D");
        var deleted = await db.EventSources.SingleAsync(row => row.ManagedKey == deletedJobKey, ct);
        Assert.NotNull(deleted.DeletedAt);
        Assert.False(deleted.Enabled);
        Assert.Equal(sourceVersionBefore + 1, deleted.Version);
        var deletedSchedule = await db.EventSchedules.SingleAsync(row => row.CallerKey == $"source:{deleted.Id}", ct);
        Assert.Equal(EventScheduleState.Cancelled, deletedSchedule.State);
        Assert.Null(deletedSchedule.DueAt);
        Assert.False(await db.EventSubscriptions.AnyAsync(row => row.ManagedKey == deletedJobKey, ct));
        var other = await db.EventSources.SingleAsync(row => row.ManagedKey == otherJobKey, ct);
        Assert.Null(other.DeletedAt);
        var otherSchedule = await db.EventSchedules.SingleAsync(row => row.CallerKey == $"source:{other.Id}", ct);
        Assert.Equal(EventScheduleState.Active, otherSchedule.State);
        Assert.True(await db.EventSubscriptions.AnyAsync(row => row.ManagedKey == otherJobKey, ct));
    }

    private static void SeedEventJob(AgwDbContext db, Guid projectId, Guid jobId, long sourceVersion)
    {
        var sourceId = Guid.CreateVersion7();
        var key = $"source:{sourceId}";
        db.EventSources.Add(
            new EventSource
            {
                Id = sourceId,
                Kind = EventSourceKind.Timer,
                ManagedBy = EventManagedBy.Job,
                ManagedKey = jobId.ToString("D"),
                CreateBy = "tester",
                Enabled = true,
                Version = sourceVersion,
            }
        );
        db.EventSubscriptions.Add(
            new EventSubscription
            {
                Id = Guid.CreateVersion7(),
                ProjectId = projectId,
                ManagedBy = EventManagedBy.Job,
                ManagedKey = jobId.ToString("D"),
                CreateBy = "tester",
                Enabled = true,
            }
        );
        db.EventSchedules.Add(
            new EventSchedule
            {
                Id = Guid.CreateVersion7(),
                CallerKey = key,
                BusinessKey = key,
                CreateBy = "tester",
                State = EventScheduleState.Active,
                DueAt = TimeProvider.System.GetUtcNow().AddHours(1),
            }
        );
    }
}
