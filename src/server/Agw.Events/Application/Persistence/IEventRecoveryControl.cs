namespace Agw.Events.Application.Persistence;

public interface IEventRecoveryControl
{
    Task<IReadOnlyList<UnpublishedEventRecord>> ListAsync(CancellationToken cancellationToken);
    Task RepublishAsync(string topic, Guid entityId, CancellationToken cancellationToken);
}

public sealed record UnpublishedEventRecord
{
    public required string Topic { get; init; }
    public required Guid EntityId { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset RecoverAfter { get; init; }
}
