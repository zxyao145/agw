using System.Text.Json;

namespace Agw.Projects.Contracts.History;

public enum ConversationMessageState : short
{
    Open,
    Completed,
    Interrupted,
    Failed,
}

public sealed record ConversationMessageWriteScope
{
    public required Guid ProjectId { get; init; }
    public required string ContextId { get; init; }
    public required int Generation { get; init; }
    public required Guid ProducerId { get; init; }
    public Guid? TurnId { get; init; }
    public string? HistoryScope { get; init; }
    public string? NodeName { get; init; }

    /// <summary>Carries the session's binding so snapshot writes keep the existing create-or-reject rules.</summary>
    public bool IsExecutionBound { get; init; }
}

public sealed record ConversationMessageSnapshot
{
    public required Guid MessageId { get; init; }
    public required ConversationMessageState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Payload { get; init; }
    public string? Author { get; init; }
    public required Dictionary<string, JsonElement> Metadata { get; init; }
}

/// <summary>An execution-local projection. Acknowledge only the exact captured objects after a successful write.</summary>
public interface IConversationMessageSource
{
    IReadOnlyList<Guid> GetPendingMessageIds();
    IReadOnlyList<ConversationMessageSnapshot> CapturePending();
    void Acknowledge(IReadOnlyList<ConversationMessageSnapshot> snapshots);
}

/// <summary>Projects owns authorization, ordering, buffering, and persistence of complete message snapshots.</summary>
public interface IConversationMessageWriter
{
    Task ScheduleAsync(
        ConversationMessageWriteScope scope,
        IConversationMessageSource source,
        long changedBytes,
        CancellationToken cancellationToken
    );

    Task UpsertAsync(
        ConversationMessageWriteScope scope,
        IReadOnlyList<ConversationMessageSnapshot> snapshots,
        CancellationToken cancellationToken
    );
}
