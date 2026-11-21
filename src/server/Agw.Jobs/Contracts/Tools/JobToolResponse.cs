namespace Agw.Jobs.Contracts.Tools;

public sealed record JobToolResponse
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
