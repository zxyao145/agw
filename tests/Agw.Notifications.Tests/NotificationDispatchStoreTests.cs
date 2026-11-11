using Agw.Infrastructure.Notifications;
using Agw.Notifications.Configuration;
using Agw.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
using EntityNotificationDispatchAttemptResult = Agw.Shared.Data.Entities.Notifications.NotificationDispatchAttemptResult;
using EntityNotificationDispatchState = Agw.Shared.Data.Entities.Notifications.NotificationDispatchState;
using EntityNotificationKind = Agw.Shared.Data.Entities.Notifications.NotificationKind;

namespace Agw.Notifications.Tests;

public sealed class NotificationDispatchStoreTests
{
    [Fact]
    public async Task Claim_RenewsLeaseAndTakesOverAfterExpiry()
    {
        await using var fixture = await NotificationTestFixture.CreateAsync(TestContext.Current.CancellationToken);
        using var owner = fixture.PushUser("owner-1");
        var seedScope = fixture.CreateScope();
        var notification = NotificationAppServiceTests.NewEntityNotification("claim-1", EntityNotificationKind.Notice);
        var dispatch = NotificationAppServiceTests.NewDispatch(
            notification.Id,
            $"ext:{Guid.CreateVersion7():D}:cards:target-1"
        );
        await seedScope.Transaction.CreateAsync(
            notification,
            question: null,
            [dispatch],
            TestContext.Current.CancellationToken
        );
        var options = new NotificationDispatchOptions { ClaimDuration = TimeSpan.FromSeconds(60) };
        var workerScope = fixture.CreateScope();
        var store = CreateStore(workerScope, fixture, options);
        var now = fixture.TimeProvider.GetUtcNow();

        var initialClaim = await store.TryClaimAsync(dispatch.Id, "host-a", TestContext.Current.CancellationToken);
        fixture.TimeProvider.SetUtcNow(now.AddSeconds(30));
        var renewed = await store.RenewAsync(
            dispatch.Id,
            initialClaim!.AttemptId,
            "host-a",
            TestContext.Current.CancellationToken
        );
        fixture.TimeProvider.SetUtcNow(now.AddSeconds(91));
        var expiredRenewal = await store.RenewAsync(
            dispatch.Id,
            initialClaim!.AttemptId,
            "host-a",
            TestContext.Current.CancellationToken
        );
        var takeover = await store.TryClaimAsync(dispatch.Id, "host-b", TestContext.Current.CancellationToken);

        Assert.NotNull(initialClaim);
        Assert.True(renewed);
        Assert.False(expiredRenewal);
        Assert.NotNull(takeover);
        Assert.Equal(dispatch.Id, takeover.Dispatch.Id);
        Assert.Equal(2, takeover.Dispatch.Attempt);
        Assert.Equal(
            2,
            await workerScope.DbContext.NotificationDispatchAttempts.CountAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task ExpiredClaimWithSameHost_CannotRenewOrCompleteAfterReclaim()
    {
        await using var fixture = await NotificationTestFixture.CreateAsync(TestContext.Current.CancellationToken);
        using var owner = fixture.PushUser("owner-1");
        var seedScope = fixture.CreateScope();
        var notification = NotificationAppServiceTests.NewEntityNotification(
            "claim-fence-1",
            EntityNotificationKind.Notice
        );
        var dispatch = NotificationAppServiceTests.NewDispatch(
            notification.Id,
            $"ext:{Guid.CreateVersion7():D}:cards:target-1"
        );
        await seedScope.Transaction.CreateAsync(
            notification,
            question: null,
            [dispatch],
            TestContext.Current.CancellationToken
        );
        var options = new NotificationDispatchOptions { ClaimDuration = TimeSpan.FromSeconds(60) };
        var oldWorkerScope = fixture.CreateScope();
        var oldStore = CreateStore(oldWorkerScope, fixture, options);
        var oldClaim = await oldStore.TryClaimAsync(dispatch.Id, "host-a", TestContext.Current.CancellationToken);
        Assert.NotNull(oldClaim);

        fixture.TimeProvider.SetUtcNow(fixture.TimeProvider.GetUtcNow().AddSeconds(61));
        var newWorkerScope = fixture.CreateScope();
        var newStore = CreateStore(newWorkerScope, fixture, options);
        var newClaim = await newStore.TryClaimAsync(dispatch.Id, "host-a", TestContext.Current.CancellationToken);
        Assert.NotNull(newClaim);

        var oldRenewal = await oldStore.RenewAsync(
            dispatch.Id,
            oldClaim.AttemptId,
            "host-a",
            TestContext.Current.CancellationToken
        );
        var oldCompletion = await oldStore.CompleteAsync(
            dispatch.Id,
            oldClaim.AttemptId,
            "host-a",
            oldClaim.Dispatch.MessageVersion,
            EntityNotificationDispatchAttemptResult.Sent,
            externalMessageId: "stale-remote",
            error: null,
            TestContext.Current.CancellationToken
        );
        var currentDispatch = await newWorkerScope
            .DbContext.NotificationDispatches.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.False(oldRenewal);
        Assert.False(oldCompletion);
        Assert.Equal(EntityNotificationDispatchState.Sending, currentDispatch.State);
        Assert.Null(currentDispatch.ExternalMessageId);
        Assert.Equal(
            EntityNotificationDispatchAttemptResult.Sending,
            (
                await newWorkerScope
                    .DbContext.NotificationDispatchAttempts.AsNoTracking()
                    .SingleAsync(attempt => attempt.Id == oldClaim.AttemptId, TestContext.Current.CancellationToken)
            ).Result
        );
        Assert.Equal(newClaim.AttemptId, currentDispatch.ClaimAttemptId);
        Assert.Equal(
            2,
            await newWorkerScope.DbContext.NotificationDispatchAttempts.CountAsync(
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task Complete_VersionAdvancedDuringSend_QueuesUpdate()
    {
        await using var fixture = await NotificationTestFixture.CreateAsync(TestContext.Current.CancellationToken);
        using var owner = fixture.PushUser("owner-1");
        var seedScope = fixture.CreateScope();
        var notification = NotificationAppServiceTests.NewEntityNotification(
            "version-1",
            EntityNotificationKind.Notice
        );
        var dispatch = NotificationAppServiceTests.NewDispatch(
            notification.Id,
            $"ext:{Guid.CreateVersion7():D}:cards:target-1"
        );
        await seedScope.Transaction.CreateAsync(
            notification,
            question: null,
            [dispatch],
            TestContext.Current.CancellationToken
        );
        var workerScope = fixture.CreateScope();
        var store = CreateStore(workerScope, fixture, new NotificationDispatchOptions());
        var claim = await store.TryClaimAsync(dispatch.Id, "host-a", TestContext.Current.CancellationToken);
        Assert.NotNull(claim);

        var updateScope = fixture.CreateScope();
        var updater = updateScope.CreateAppService();
        await updater.UpdateAsync(
            new NotificationUpdate
            {
                NotificationId = notification.Id,
                Version = 1,
                Title = "Updated title",
                Text = "Updated content.",
                State = NotificationState.Open,
            },
            TestContext.Current.CancellationToken
        );

        var completed = await store.CompleteAsync(
            dispatch.Id,
            claim.AttemptId,
            "host-a",
            sentVersion: 1,
            EntityNotificationDispatchAttemptResult.Sent,
            externalMessageId: "remote-1",
            error: null,
            TestContext.Current.CancellationToken
        );
        var storedDispatch = await workerScope
            .DbContext.NotificationDispatches.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.True(completed);
        Assert.Equal(EntityNotificationDispatchState.UpdatePending, storedDispatch.State);
        Assert.Equal(2, storedDispatch.MessageVersion);
        Assert.Equal(1, storedDispatch.SentVersion);
        Assert.Equal("remote-1", storedDispatch.ExternalMessageId);
    }

    [Fact]
    public async Task RecoverDue_RetriesFailedDispatchThenManualRetryReopensParkedDispatch()
    {
        await using var fixture = await NotificationTestFixture.CreateAsync(TestContext.Current.CancellationToken);
        using var owner = fixture.PushUser("owner-1");
        var seedScope = fixture.CreateScope();
        var notification = NotificationAppServiceTests.NewEntityNotification("retry-1", EntityNotificationKind.Notice);
        var dispatch = NotificationAppServiceTests.NewDispatch(
            notification.Id,
            $"ext:{Guid.CreateVersion7():D}:cards:target-1"
        );
        await seedScope.Transaction.CreateAsync(
            notification,
            question: null,
            [dispatch],
            TestContext.Current.CancellationToken
        );
        var options = new NotificationDispatchOptions
        {
            MaxAttempts = 2,
            BaseBackoff = TimeSpan.FromSeconds(10),
            MaxBackoff = TimeSpan.FromMinutes(1),
            RecoveryStaleAfter = TimeSpan.FromMinutes(2),
        };
        var firstWorkerScope = fixture.CreateScope();
        var firstStore = CreateStore(firstWorkerScope, fixture, options);
        var firstClaim = await firstStore.TryClaimAsync(dispatch.Id, "host-a", TestContext.Current.CancellationToken);
        Assert.NotNull(firstClaim);
        Assert.True(
            await firstStore.CompleteAsync(
                dispatch.Id,
                firstClaim.AttemptId,
                "host-a",
                firstClaim.Dispatch.MessageVersion,
                EntityNotificationDispatchAttemptResult.Failed,
                externalMessageId: null,
                error: "temporary failure",
                TestContext.Current.CancellationToken
            )
        );

        fixture.TimeProvider.SetUtcNow(fixture.TimeProvider.GetUtcNow().AddSeconds(11));
        var recoveryScope = fixture.CreateScope();
        var recoveryStore = CreateStore(recoveryScope, fixture, options);
        Assert.Equal(1, await recoveryStore.RecoverDueAsync(TestContext.Current.CancellationToken));
        var recoveredDispatch = await recoveryScope
            .DbContext.NotificationDispatches.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EntityNotificationDispatchState.Pending, recoveredDispatch.State);
        Assert.Equal(fixture.TimeProvider.GetUtcNow().Add(options.RecoveryStaleAfter), recoveredDispatch.RecoverAfter);

        var secondWorkerScope = fixture.CreateScope();
        var secondStore = CreateStore(secondWorkerScope, fixture, options);
        var secondClaim = await secondStore.TryClaimAsync(dispatch.Id, "host-a", TestContext.Current.CancellationToken);
        Assert.NotNull(secondClaim);
        Assert.True(
            await secondStore.CompleteAsync(
                dispatch.Id,
                secondClaim.AttemptId,
                "host-a",
                secondClaim.Dispatch.MessageVersion,
                EntityNotificationDispatchAttemptResult.Failed,
                externalMessageId: null,
                error: "second failure",
                TestContext.Current.CancellationToken
            )
        );

        var parked = await secondWorkerScope
            .DbContext.NotificationDispatches.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EntityNotificationDispatchState.Parked, parked.State);
        var retryScope = fixture.CreateScope();
        await retryScope
            .CreateAppService(options)
            .RetryDispatchAsync(dispatch.Id, TestContext.Current.CancellationToken);
        var requeued = await retryScope
            .DbContext.NotificationDispatches.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EntityNotificationDispatchState.Pending, requeued.State);
        Assert.Equal(0, requeued.Attempt);
        Assert.Null(requeued.LastError);
    }

    [Fact]
    public async Task RecoverDueAsync_OutboxInsertFails_RollsBackDispatchState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await NotificationTestFixture.CreateAsync(cancellationToken);
        using var owner = fixture.PushUser("owner-1");
        var seedScope = fixture.CreateScope();
        var notification = NotificationAppServiceTests.NewEntityNotification(
            "retry-rollback",
            EntityNotificationKind.Notice
        );
        var dispatch = NotificationAppServiceTests.NewDispatch(
            notification.Id,
            $"ext:{Guid.CreateVersion7():D}:cards:target-1"
        );
        dispatch.State = EntityNotificationDispatchState.Failed;
        dispatch.NextAttemptAt = fixture.TimeProvider.GetUtcNow();
        await seedScope.Transaction.CreateAsync(notification, question: null, [dispatch], cancellationToken);
        await seedScope.DbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER reject_retry_outbox BEFORE INSERT ON event_outbox
            BEGIN SELECT RAISE(ABORT, 'retry outbox insert rejected'); END;
            """,
            cancellationToken
        );
        var recoveryScope = fixture.CreateScope();
        var store = CreateStore(recoveryScope, fixture, new NotificationDispatchOptions());

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => store.RecoverDueAsync(cancellationToken));

        Assert.Contains("retry outbox insert rejected", error.InnerException?.Message);
        var readScope = fixture.CreateScope();
        var stored = await readScope.DbContext.NotificationDispatches.AsNoTracking().SingleAsync(cancellationToken);
        Assert.Equal(EntityNotificationDispatchState.Failed, stored.State);
        Assert.Equal(dispatch.NextAttemptAt, stored.NextAttemptAt);
        Assert.Equal(dispatch.RecoverAfter, stored.RecoverAfter);
        Assert.Equal(1, await readScope.DbContext.EventOutbox.CountAsync(cancellationToken));
    }

    private static NotificationDispatchStore CreateStore(
        NotificationTestFixture.NotificationTestScope scope,
        NotificationTestFixture fixture,
        NotificationDispatchOptions options
    ) => new(scope.DbContext, scope.Transaction, fixture.TimeProvider, Options.Create(options));
}
