namespace Agw.Jobs.Contracts;

public sealed record JobResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required bool Enabled { get; init; }
    public required Guid ProjectId { get; init; }
    public required AgentRuntimeType TargetKind { get; init; }
    public required Guid AgentId { get; init; }
    public required string Instructions { get; init; }
    public required JobScheduleResponse Schedule { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public required bool ScheduleCompleted { get; init; }
    public JobDeliveryResponse? LastDelivery { get; init; }
}

public sealed record JobScheduleResponse
{
    public required string Kind { get; init; }
    public DateTimeOffset? At { get; init; }
    public string? Interval { get; init; }
    public string? Cron { get; init; }
}

public sealed record JobDeliveryResponse
{
    public required Guid Id { get; init; }
    public required int State { get; init; }
    public required int Attempt { get; init; }
    public string? LastError { get; init; }
}

public sealed record JobRunResponse
{
    public required Guid Id { get; init; }
    public required Guid DeliveryId { get; init; }
    public required int Attempt { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required int Result { get; init; }
    public string? Error { get; init; }
    public Guid? ConversationId { get; init; }
}
