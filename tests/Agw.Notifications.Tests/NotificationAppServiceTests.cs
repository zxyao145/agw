using Agw.Auth.Contracts;
using Agw.Events.Contracts;
using Agw.Notifications.Application;
using Agw.Notifications.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using EntityNotification = Agw.Shared.Data.Entities.Notifications.Notification;
using EntityNotificationDispatch = Agw.Shared.Data.Entities.Notifications.NotificationDispatch;
using EntityNotificationDispatchState = Agw.Shared.Data.Entities.Notifications.NotificationDispatchState;
using EntityNotificationKind = Agw.Shared.Data.Entities.Notifications.NotificationKind;
using EntityNotificationQuestion = Agw.Shared.Data.Entities.Notifications.NotificationQuestion;
using EntityNotificationState = Agw.Shared.Data.Entities.Notifications.NotificationState;

namespace Agw.Notifications.Tests;

public sealed class NotificationAppServiceTests
{
    [Fact]
    public async Task Create_SameIdempotencyKey_ReturnsExistingNotificationAndDispatch()
    {
        await using var fixture = await NotificationTestFixture.CreateAsync(TestContext.Current.CancellationToken);
        var scope = fixture.CreateScope();
        using var owner = fixture.PushUser("owner-1");
        var appService = scope.CreateAppService();
        var request = new NotificationRequest
        {
            IdempotencyKey = "notice-1",
            Kind = NotificationKind.Notice,
            Title = "Ready",
            Text = "The task is ready.",
            ChannelKeys = ["desktop"],
            CorrelationKey = "task-1",
        };

        var firstId = await appService.CreateAsync(request, TestContext.Current.CancellationToken);
        var duplicateId = await appService.CreateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(firstId, duplicateId);
        Assert.Single(
            await scope.DbContext.Notifications.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)
        );
        Assert.Single(
            await scope
                .DbContext.NotificationDispatches.AsNoTracking()
                .ToListAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task StageAsync_EventTransactionStagesNotificationWithoutSaving()
    {
        await using var fixture = await NotificationTestFixture.CreateAsync(TestContext.Current.CancellationToken);
        var scope = fixture.CreateScope();
        using var owner = fixture.PushUser("owner-1");
        var appService = scope.CreateAppService();
        var staging = new NotificationStagingFacade(appService);

        var id = await staging.StageAsync(
            new NotificationRequest
            {
                IdempotencyKey = "staged-1",
                Kind = NotificationKind.Notice,
                Title = "Staged",
                Text = "Saved with the calling event.",
                CorrelationKey = "event-1",
            },
            TestContext.Current.CancellationToken
        );

        Assert.Empty(
            await scope.DbContext.Notifications.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)
        );
        Assert.Contains(scope.DbContext.Notifications.Local, notification => notification.Id == id);

        await scope.DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(
            await scope.DbContext.Notifications.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Answer_OpenQuestion_StoresAnswerAndPublishesLineageEvent()
    {
        await using var fixture = await NotificationTestFixture.CreateAsync(TestContext.Current.CancellationToken);
        var scope = fixture.CreateScope();
        using var owner = fixture.PushUser("owner-1");
        var parent = await scope.EventStaging.StageAsync(
            new EventStagingRequest
            {
                PublisherKey = "agw.notifications",
                Event = new CloudEventDraft
                {
                    Id = "question-parent",
                    Type = "agw.notification.created",
                    Time = fixture.TimeProvider.GetUtcNow(),
                },
            },
            TestContext.Current.CancellationToken
        );
        await scope.DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var appService = scope.CreateAppService();
        var notificationId = await appService.CreateAsync(
            new NotificationRequest
            {
                IdempotencyKey = "question-1",
                Kind = NotificationKind.Question,
                Title = "Merge this change?",
                Text = "Choose an option.",
                Options = [new NotificationOption("Approve", "approve"), new NotificationOption("Decline", "decline")],
                AllowText = true,
                CorrelationKey = "task-1",
                ParentEventId = parent.EventId,
            },
            TestContext.Current.CancellationToken
        );

        var result = await appService.SubmitAnswerAsync(
            new NotificationAnswer
            {
                NotificationId = notificationId,
                Option = 0,
                Text = "Approved for staging.",
            },
            TestContext.Current.CancellationToken
        );

        var notification = await scope
            .DbContext.Notifications.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        var question = await scope
            .DbContext.NotificationQuestions.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        var answerEvent = await scope
            .DbContext.Events.AsNoTracking()
            .SingleAsync(
                record => record.CeId == $"{notificationId:D}:answered",
                TestContext.Current.CancellationToken
            );
        var parentEvent = await scope
            .DbContext.Events.AsNoTracking()
            .SingleAsync(record => record.Id == parent.EventId, TestContext.Current.CancellationToken);

        Assert.Equal(EntityNotificationState.Answered, notification.State);
        Assert.Equal(2, notification.Version);
        Assert.Equal(0, question.AnswerOption);
        Assert.Equal("Approved for staging.", question.AnswerText);
        Assert.Equal(notificationId, result.NotificationId);
        Assert.Equal(NotificationState.Answered, result.State);
        Assert.Equal(parent.EventId, answerEvent.ParentEventId);
        Assert.Equal(parentEvent.ChainId, answerEvent.ChainId);
        Assert.Equal(parentEvent.Hops, answerEvent.Hops);
    }

    [Fact]
    public async Task Answer_SecondAnswer_ReturnsCurrentAnswerWithConflict()
    {
        await using var fixture = await NotificationTestFixture.CreateAsync(TestContext.Current.CancellationToken);
        var scope = fixture.CreateScope();
        using var owner = fixture.PushUser("owner-1");
        var appService = scope.CreateAppService();
        var notificationId = await appService.CreateAsync(
            QuestionRequest("question-2"),
            TestContext.Current.CancellationToken
        );
        var answer = new NotificationAnswer { NotificationId = notificationId, Option = 0 };
        await appService.SubmitAnswerAsync(answer, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            appService.SubmitAnswerAsync(answer, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.NotificationQuestionAlreadyAnswered.Code, exception.Code);
        var existingAnswer = Assert.IsType<NotificationAnswerResult>(exception.Data[nameof(NotificationAnswerResult)]);
        Assert.Equal(notificationId, existingAnswer.NotificationId);
        Assert.Equal(0, existingAnswer.Option);
    }

    [Fact]
    public async Task Get_ForeignOwner_DoesNotReturnNotification()
    {
        await using var fixture = await NotificationTestFixture.CreateAsync(TestContext.Current.CancellationToken);
        var scope = fixture.CreateScope();
        var appService = scope.CreateAppService();
        Guid id;
        using (fixture.PushUser("owner-1"))
        {
            id = await appService.CreateAsync(NoticeRequest("notice-3"), TestContext.Current.CancellationToken);
        }

        using (fixture.PushUser("owner-2"))
        {
            Assert.Null(await appService.GetAsync(id, TestContext.Current.CancellationToken));
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                appService.SubmitAnswerAsync(
                    new NotificationAnswer { NotificationId = id, Option = 0 },
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        }
    }

    [Fact]
    public async Task Update_ExactReplayAfterLaterUpdateAndAnswer_IsIdempotent()
    {
        await using var fixture = await NotificationTestFixture.CreateAsync(TestContext.Current.CancellationToken);
        using var owner = fixture.PushUser("owner-1");
        var createScope = fixture.CreateScope();
        var notificationId = await createScope
            .CreateAppService()
            .CreateAsync(QuestionRequest("update-replay-1"), TestContext.Current.CancellationToken);
        var firstUpdate = new NotificationUpdate
        {
            NotificationId = notificationId,
            Version = 1,
            Title = "First update",
            Text = "First content.",
            State = NotificationState.Open,
        };
        await fixture.CreateScope().CreateAppService().UpdateAsync(firstUpdate, TestContext.Current.CancellationToken);
        await fixture
            .CreateScope()
            .CreateAppService()
            .UpdateAsync(
                firstUpdate with
                {
                    Version = 2,
                    Title = "Second update",
                    Text = "Second content.",
                },
                TestContext.Current.CancellationToken
            );
        await fixture
            .CreateScope()
            .CreateAppService()
            .SubmitAnswerAsync(
                new NotificationAnswer { NotificationId = notificationId, Option = 0 },
                TestContext.Current.CancellationToken
            );

        await fixture.CreateScope().CreateAppService().UpdateAsync(firstUpdate, TestContext.Current.CancellationToken);
        var conflictingReplay = await Assert.ThrowsAsync<AgwException>(() =>
            fixture
                .CreateScope()
                .CreateAppService()
                .UpdateAsync(firstUpdate with { Text = "Different content." }, TestContext.Current.CancellationToken)
        );
        var current = await fixture
            .CreateScope()
            .DbContext.Notifications.AsNoTracking()
            .SingleAsync(notification => notification.Id == notificationId, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorCodes.InvalidParam.Code, conflictingReplay.Code);
        Assert.Equal(4, current.Version);
        Assert.Equal(EntityNotificationState.Answered, current.State);
        Assert.Equal("Second update", current.Title);
    }

    [Fact]
    public async Task Update_ForeignOwner_DoesNotAcceptReplay()
    {
        await using var fixture = await NotificationTestFixture.CreateAsync(TestContext.Current.CancellationToken);
        var scope = fixture.CreateScope();
        Guid notificationId;
        using (fixture.PushUser("owner-1"))
        {
            notificationId = await scope
                .CreateAppService()
                .CreateAsync(NoticeRequest("update-owner-1"), TestContext.Current.CancellationToken);
            await scope
                .CreateAppService()
                .UpdateAsync(
                    new NotificationUpdate
                    {
                        NotificationId = notificationId,
                        Version = 1,
                        Title = "Update",
                        Text = "Owner replay.",
                        State = NotificationState.Open,
                    },
                    TestContext.Current.CancellationToken
                );
        }

        using (fixture.PushUser("owner-2"))
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                scope
                    .CreateAppService()
                    .UpdateAsync(
                        new NotificationUpdate
                        {
                            NotificationId = notificationId,
                            Version = 1,
                            Title = "Update",
                            Text = "Foreign replay.",
                            State = NotificationState.Open,
                        },
                        TestContext.Current.CancellationToken
                    )
            );

            Assert.Equal(ErrorCodes.NotificationNotFound.Code, exception.Code);
            Assert.Empty(
                await scope
                    .DbContext.NotificationUpdateReceipts.AsNoTracking()
                    .ToListAsync(TestContext.Current.CancellationToken)
            );
        }

        Assert.Empty(
            await scope
                .DbContext.NotificationUpdateReceipts.AsNoTracking()
                .ToListAsync(TestContext.Current.CancellationToken)
        );
        using (fixture.PushUser("owner-1"))
            Assert.Single(
                await scope
                    .DbContext.NotificationUpdateReceipts.AsNoTracking()
                    .ToListAsync(TestContext.Current.CancellationToken)
            );
    }

    [Fact]
    public async Task Answer_ChannelMessageWithoutExtensionCaller_IsForbidden()
    {
        await using var fixture = await NotificationTestFixture.CreateAsync(TestContext.Current.CancellationToken);
        var scope = fixture.CreateScope();
        using var owner = fixture.PushUser("owner-1");
        var appService = scope.CreateAppService();
        var notification = NewEntityNotification("question-4", EntityNotificationKind.Question);
        var dispatch = NewDispatch(notification.Id, $"ext:{Guid.CreateVersion7():D}:cards:target-1");
        await scope.Transaction.CreateAsync(
            notification,
            NewQuestion(notification.Id),
            [dispatch],
            TestContext.Current.CancellationToken
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            appService.SubmitAnswerAsync(
                new NotificationAnswer
                {
                    NotificationId = notification.Id,
                    Option = 0,
                    ChannelKey = dispatch.ChannelKey,
                    ExternalMessageId = "message-1",
                },
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.NotificationAnswerForbidden.Code, exception.Code);
    }

    internal static NotificationRequest NoticeRequest(string key) =>
        new()
        {
            IdempotencyKey = key,
            Kind = NotificationKind.Notice,
            Title = "Notice",
            Text = "A notice.",
            CorrelationKey = key,
        };

    internal static NotificationRequest QuestionRequest(string key) =>
        new()
        {
            IdempotencyKey = key,
            Kind = NotificationKind.Question,
            Title = "Question",
            Text = "Choose one.",
            Options = [new NotificationOption("Approve", "approve")],
            CorrelationKey = key,
        };

    internal static EntityNotification NewEntityNotification(string key, EntityNotificationKind kind) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            CreateBy = UserInfoUtil.RequiredUserId,
            Kind = kind,
            Title = "Dispatch test",
            Text = "Dispatch content.",
            CallerKey = "agw.events",
            IdempotencyKey = key,
            CorrelationKey = key,
            ChainId = Guid.CreateVersion7(),
            State = EntityNotificationState.Open,
            ChangedAt = new DateTimeOffset(2026, 11, 8, 1, 0, 0, TimeSpan.Zero),
            Version = 1,
        };

    internal static EntityNotificationQuestion NewQuestion(Guid notificationId) =>
        new() { NotificationId = notificationId, OptionsJson = "[{\"label\":\"Approve\",\"value\":\"approve\"}]" };

    internal static EntityNotificationDispatch NewDispatch(Guid notificationId, string channelKey) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            NotificationId = notificationId,
            ChannelKey = channelKey,
            State = EntityNotificationDispatchState.Pending,
            MessageVersion = 1,
            RecoverAfter = new DateTimeOffset(2026, 11, 8, 1, 0, 0, TimeSpan.Zero),
        };
}
