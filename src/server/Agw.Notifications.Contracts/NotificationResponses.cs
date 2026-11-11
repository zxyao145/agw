namespace Agw.Notifications.Contracts;

public sealed record NotificationListItem
{
    public required Guid Id { get; init; }

    public required NotificationKind Kind { get; init; }

    public required NotificationState State { get; init; }

    public required string Title { get; init; }

    public required string Text { get; init; }

    public required string? Link { get; init; }

    public required DateTimeOffset? ReadAt { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }

    public required long Version { get; init; }
}

public sealed record NotificationQuestionResponse
{
    public required IReadOnlyList<NotificationOption> Options { get; init; }

    public required int? Recommend { get; init; }

    public required bool AllowText { get; init; }

    public required int? AnswerOption { get; init; }

    public required string? AnswerText { get; init; }

    public required string? AnsweredBy { get; init; }

    public required string? AnsweredVia { get; init; }

    public required DateTimeOffset? AnsweredAt { get; init; }
}

public sealed record NotificationDispatchAttemptResponse
{
    public required Guid Id { get; init; }

    public required int Attempt { get; init; }

    public required long MessageVersion { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset? FinishedAt { get; init; }

    public required NotificationDispatchAttemptResult Result { get; init; }

    public required string? ExternalMessageId { get; init; }

    public required string? Error { get; init; }
}

public sealed record NotificationDispatchResponse
{
    public required Guid Id { get; init; }

    public required string ChannelKey { get; init; }

    public required NotificationDispatchState State { get; init; }

    public required string? ExternalMessageId { get; init; }

    public required long MessageVersion { get; init; }

    public required long SentVersion { get; init; }

    public required int Attempt { get; init; }

    public required DateTimeOffset? NextAttemptAt { get; init; }

    public required string? LastError { get; init; }

    public required IReadOnlyList<NotificationDispatchAttemptResponse> Attempts { get; init; }
}

public sealed record NotificationDetail
{
    public required Guid Id { get; init; }

    public required NotificationKind Kind { get; init; }

    public required NotificationState State { get; init; }

    public required string Title { get; init; }

    public required string Text { get; init; }

    public required string? Link { get; init; }

    public required DateTimeOffset? ReadAt { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }

    public required long Version { get; init; }

    public required NotificationQuestionResponse? Question { get; init; }

    public required IReadOnlyList<NotificationDispatchResponse> Dispatches { get; init; }
}

public sealed record NotificationStreamItem
{
    public required Guid Id { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required NotificationKind Kind { get; init; }

    public required NotificationState State { get; init; }

    public required string Title { get; init; }

    public required string Text { get; init; }

    public required string? Link { get; init; }

    public required DateTimeOffset? ReadAt { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }

    public required long Version { get; init; }
}

public sealed record NotificationChannelResponse
{
    public required string ChannelKey { get; init; }

    public required string Name { get; init; }

    public required Guid? ExtensionId { get; init; }

    public required string? ChannelId { get; init; }

    public required string? TargetId { get; init; }
}
