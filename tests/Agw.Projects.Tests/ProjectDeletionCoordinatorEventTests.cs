using Agw.Infrastructure.Data;
using Agw.Projects.Application.Persistence;
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
}
