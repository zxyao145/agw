namespace Agw.Notifications.Contracts;

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

public sealed record NotificationOption(string Label, string Value);

public sealed record NotificationRequest
{
    public required string IdempotencyKey { get; init; }

    public required NotificationKind Kind { get; init; }

    public required string Title { get; init; }

    public required string Text { get; init; }

    public string? Link { get; init; }

    public IReadOnlyList<NotificationOption> Options { get; init; } = [];

    public int? Recommend { get; init; }

    public bool AllowText { get; init; }

    public IReadOnlyList<string> ChannelKeys { get; init; } = [];

    public required string CorrelationKey { get; init; }

    public Guid? ParentEventId { get; init; }
}

public sealed record NotificationUpdate
{
    public required Guid NotificationId { get; init; }

    public required long Version { get; init; }

    public required string Title { get; init; }

    public required string Text { get; init; }

    public string? Link { get; init; }

    public required NotificationState State { get; init; }
}

public sealed record NotificationAnswer
{
    public required Guid NotificationId { get; init; }

    public int? Option { get; init; }

    public string? Text { get; init; }

    public string? ChannelKey { get; init; }

    public string? ExternalMessageId { get; init; }
}

public sealed record NotificationAnswerResult
{
    public required Guid NotificationId { get; init; }

    public required NotificationState State { get; init; }

    public required long Version { get; init; }

    public int? Option { get; init; }

    public string? Text { get; init; }

    public string? AnsweredBy { get; init; }

    public string? AnsweredVia { get; init; }

    public DateTimeOffset? AnsweredAt { get; init; }
}

public interface INotificationFacade
{
    Task<Guid> CreateAsync(NotificationRequest request, CancellationToken cancellationToken = default);

    Task UpdateAsync(NotificationUpdate update, CancellationToken cancellationToken = default);
}

public interface INotificationStaging
{
    Task<Guid> StageAsync(NotificationRequest request, CancellationToken cancellationToken = default);
}

public interface INotificationAnswerFacade
{
    Task<NotificationAnswerResult> SubmitAsync(
        NotificationAnswer answer,
        CancellationToken cancellationToken = default
    );
}
