using Agw.Shared.Contracts.Events;

namespace Agw.Events.Contracts;

public interface IEventScheduler
{
    Task<ScheduleState> UpsertAsync(ScheduleRequest request, CancellationToken cancellationToken);

    Task<ScheduleState> CancelAsync(string businessKey, long requestSeq, CancellationToken cancellationToken);

    Task<ScheduleState?> GetAsync(string businessKey, CancellationToken cancellationToken);
}

public enum ScheduleKind
{
    Once,
    Interval,
    Cron,
}

public enum ScheduleStatus
{
    Active,
    Completed,
    Cancelled,
}

public sealed record ScheduleRequest
{
    public required string BusinessKey { get; init; }

    public required long RequestSeq { get; init; }

    public required ScheduleKind Kind { get; init; }

    public DateTimeOffset? At { get; init; }

    public TimeSpan? Interval { get; init; }

    public string? Cron { get; init; }

    public required string TimeZone { get; init; }

    public required CloudEventDraft Event { get; init; }

    public Guid? ParentEventId { get; init; }
}

public sealed record ScheduleState
{
    public required Guid ScheduleId { get; init; }

    public required ScheduleStatus Status { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public required long RequestSeq { get; init; }

    public required long Revision { get; init; }

    public required bool Accepted { get; init; }
}
