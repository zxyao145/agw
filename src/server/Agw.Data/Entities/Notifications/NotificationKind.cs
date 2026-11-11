namespace Agw.Shared.Data.Entities.Notifications;

public enum NotificationKind
{
    Notice = 0,
    Question = 1,
}

public enum NotificationState
{
    Open = 0,
    Answered = 1,
    Resolved = 2,
    Closed = 3,
}

public enum NotificationDispatchState
{
    Pending = 0,
    Sending = 1,
    Sent = 2,
    UpdatePending = 3,
    Failed = 4,
    Parked = 5,
    Unsupported = 6,
}

public enum NotificationDispatchAttemptResult
{
    Sent = 0,
    Unsupported = 1,
    Failed = 2,
    Sending = 3,
}
