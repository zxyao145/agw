namespace Agw.Events.Contracts;

public interface IEventJobFacade
{
    Task<EventJob> CreateAsync(EventJobDefinition definition, CancellationToken cancellationToken);
    Task<EventJob> UpdateAsync(
        Guid jobId,
        EventJobDefinition definition,
        CancellationToken cancellationToken,
        Guid? expectedProjectId = null
    );
    Task<EventJob> SetEnabledAsync(Guid jobId, bool enabled, CancellationToken cancellationToken);

    /// <summary>
    /// 返回删除之前的 Job。
    /// Returns the Job as it was before deletion.
    /// </summary>
    Task<EventJob> DeleteAsync(Guid jobId, CancellationToken cancellationToken);

    Task<EventJob?> FindAsync(Guid jobId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EventJob>> ListAsync(Guid? projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EventJobRun>> ListRunsAsync(
        Guid jobId,
        int offset,
        int limit,
        CancellationToken cancellationToken
    );
    Task<int> CountAsync(CancellationToken cancellationToken);
}

public enum EventJobTargetKind
{
    Agent = 0,
    Agentflow = 1,
}

public sealed record EventJobSchedule
{
    public required string Kind { get; init; }
    public DateTimeOffset? At { get; init; }
    public string? Interval { get; init; }
    public string? Cron { get; init; }
}

public sealed record EventJobDefinition
{
    public required string Name { get; init; }
    public required bool Enabled { get; init; }
    public required Guid ProjectId { get; init; }
    public required EventJobTargetKind TargetKind { get; init; }
    public required Guid AgentId { get; init; }
    public required string Instructions { get; init; }
    public required EventJobSchedule Schedule { get; init; }
}

public sealed record EventJob
{
    public required Guid Id { get; init; }
    public required EventJobDefinition Definition { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public bool ScheduleCompleted { get; init; }
    public EventJobDelivery? LastDelivery { get; init; }
}

public sealed record EventJobDelivery
{
    public required Guid Id { get; init; }

    // State 的数值与 EventDeliveryState 相同。
    // State uses the values of EventDeliveryState.
    public required int State { get; init; }
    public required int Attempt { get; init; }
    public string? LastError { get; init; }
}

public sealed record EventJobRun
{
    public required Guid Id { get; init; }
    public required Guid DeliveryId { get; init; }
    public required int Attempt { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }

    // Result 的数值与 EventDeliveryAttemptResult 相同。
    // Result uses the values of EventDeliveryAttemptResult.
    public required int Result { get; init; }
    public string? Error { get; init; }
    public Guid? TurnId { get; init; }
}
