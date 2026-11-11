using Agw.Shared.Data.Entities.Notifications;
using Agw.Shared.Exceptions;

namespace Agw.Notifications.Domain.Behaviors;

public sealed class NotificationBehavior
{
    private readonly Notification _notification;

    public NotificationBehavior(Notification notification)
    {
        _notification = notification;
    }

    public void Answer(
        int? option,
        string? text,
        int optionCount,
        string answeredBy,
        string answeredVia,
        DateTimeOffset answeredAt
    )
    {
        if (_notification.State == NotificationState.Answered)
            throw new AgwException(ErrorCodes.NotificationQuestionAlreadyAnswered);
        if (_notification.Kind != NotificationKind.Question || _notification.State != NotificationState.Open)
            throw new AgwException(ErrorCodes.NotificationNotFound);
        var question = _notification.Question ?? throw new AgwException(ErrorCodes.NotificationNotFound);

        if (option is int selectedOption && (selectedOption < 0 || selectedOption >= optionCount))
            throw new AgwException(ErrorCodes.InvalidParam);
        if (option is null && (!question.AllowText || string.IsNullOrWhiteSpace(text)))
            throw new AgwException(ErrorCodes.InvalidParam);
        if (text?.Length > 4000)
            throw new AgwException(ErrorCodes.InvalidParam);

        question.AnswerOption = option;
        question.AnswerText = string.IsNullOrWhiteSpace(text) ? null : text;
        question.AnsweredBy = answeredBy;
        question.AnsweredVia = answeredVia;
        question.AnsweredAt = answeredAt;
        _notification.State = NotificationState.Answered;
        _notification.Version++;
        _notification.ChangedAt = answeredAt;
    }
}
