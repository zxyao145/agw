using Agw.Notifications.Domain.Behaviors;
using Agw.Shared.Data.Entities.Notifications;
using Agw.Shared.Exceptions;
using Xunit;

namespace Agw.Notifications.Tests.Domain;

public sealed class NotificationBehaviorTests
{
    [Fact]
    public void Answer_OpenQuestion_RecordsAnswerAndAdvancesVersion()
    {
        var changedAt = new DateTimeOffset(2026, 11, 8, 1, 0, 0, TimeSpan.Zero);
        var answeredAt = changedAt.AddMinutes(1);
        var notification = new Notification
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "user-1",
            Kind = NotificationKind.Question,
            State = NotificationState.Open,
            ChangedAt = changedAt,
            Version = 4,
        };
        var question = new NotificationQuestion
        {
            NotificationId = notification.Id,
            OptionsJson = "[{\"label\":\"Approve\",\"value\":\"approve\"}]",
            AllowText = true,
        };

        notification.Question = question;
        new NotificationBehavior(notification).Answer(0, "Looks good", 1, "user-1", "desktop", answeredAt);

        Assert.Equal(NotificationState.Answered, notification.State);
        Assert.Equal(5, notification.Version);
        Assert.Equal(answeredAt, notification.ChangedAt);
        Assert.Equal(0, question.AnswerOption);
        Assert.Equal("Looks good", question.AnswerText);
        Assert.Equal("user-1", question.AnsweredBy);
        Assert.Equal("desktop", question.AnsweredVia);
        Assert.Equal(answeredAt, question.AnsweredAt);
    }

    [Fact]
    public void Answer_AlreadyAnsweredQuestion_ThrowsConflict()
    {
        var notification = new Notification
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "user-1",
            Kind = NotificationKind.Question,
            State = NotificationState.Answered,
            ChangedAt = DateTimeOffset.UtcNow,
            Version = 2,
        };
        var question = new NotificationQuestion { NotificationId = notification.Id };

        var exception = Assert.Throws<AgwException>(() =>
            new NotificationBehavior(notification).Answer(0, null, 1, "user-1", "desktop", DateTimeOffset.UtcNow)
        );

        Assert.Equal(ErrorCodes.NotificationQuestionAlreadyAnswered.Code, exception.Code);
    }

    [Fact]
    public void Answer_TextOnlyWhenTextIsDisabled_ThrowsInvalidParam()
    {
        var notification = new Notification
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "user-1",
            Kind = NotificationKind.Question,
            State = NotificationState.Open,
            ChangedAt = DateTimeOffset.UtcNow,
            Version = 1,
        };
        var question = new NotificationQuestion
        {
            NotificationId = notification.Id,
            OptionsJson = "[{\"label\":\"Approve\",\"value\":\"approve\"}]",
            AllowText = false,
        };

        notification.Question = question;
        var exception = Assert.Throws<AgwException>(() =>
            new NotificationBehavior(notification).Answer(
                null,
                "Looks good",
                1,
                "user-1",
                "desktop",
                DateTimeOffset.UtcNow
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }
}
