namespace Agw.Extensions.Contracts;

public sealed record ExtensionNotificationChannelDescriptor
{
    public required string Id { get; init; }

    public required string Name { get; init; }
}

public enum ExtensionNotificationKind
{
    Notice,
    Question,
}

public enum ExtensionNotificationState
{
    Open,
    Answered,
    Resolved,
    Closed,
}

public sealed record NotificationOption
{
    public required string Label { get; init; }

    public required string Value { get; init; }
}

public sealed record NotificationMessage
{
    public required Guid NotificationId { get; init; }

    public required string DispatchKey { get; init; }

    public required string ChannelKey { get; init; }

    public required string IdempotencyKey { get; init; }

    public required long ContentVersion { get; init; }

    public required ExtensionNotificationKind Kind { get; init; }

    public required ExtensionNotificationState State { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    public string? Link { get; init; }

    public IReadOnlyList<NotificationOption> Options { get; init; } = [];

    public int? Recommend { get; init; }

    public bool AllowText { get; init; }

    public int? AnswerOption { get; init; }

    public string? AnswerText { get; init; }

    public string? AnsweredBy { get; init; }

    public string? AnsweredVia { get; init; }

    public DateTimeOffset? AnsweredAt { get; init; }
}

public sealed record NotificationChannelTarget
{
    public required string TargetId { get; init; }

    public required string Name { get; init; }
}

public enum NotificationSendStatus
{
    Sent,
    Unsupported,
}

public sealed record NotificationSendResult
{
    public required NotificationSendStatus Status { get; init; }

    public string? ExternalMessageId { get; init; }
}

public interface IExtensionNotificationChannel
{
    ValueTask<IReadOnlyList<NotificationChannelTarget>> ListTargetsAsync(CancellationToken cancellationToken);

    ValueTask<NotificationSendResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken);

    ValueTask<NotificationSendResult> UpdateAsync(
        NotificationMessage message,
        string externalMessageId,
        CancellationToken cancellationToken
    );
}

public enum ExtensionNotificationChannelOperation
{
    ListTargets,
    Send,
    Update,
}

public sealed record ExtensionNotificationChannelInvocation
{
    public required string ChannelId { get; init; }

    public required string UserId { get; init; }

    public required Guid InvocationId { get; init; }

    public required ExtensionNotificationChannelOperation Operation { get; init; }

    public NotificationMessage? Message { get; init; }

    public string? ExternalMessageId { get; init; }
}

public sealed record ExtensionNotificationChannelResult
{
    public IReadOnlyList<NotificationChannelTarget>? Targets { get; init; }

    public NotificationSendResult? SendResult { get; init; }
}
