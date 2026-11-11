using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Application.Persistence;

public interface IBackgroundExecutionCompletionTransaction
{
    Task<bool> RegisterCancelledAsync(BackgroundExecution tombstone, CancellationToken cancellationToken);

    Task<bool> CompleteAsync(BackgroundExecutionCompletion completion, CancellationToken cancellationToken);
}

public sealed record BackgroundExecutionCompletion
{
    public required Guid TurnId { get; init; }
    public required string OwnerUserId { get; init; }
    public required AgentExecutionState State { get; init; }
    public string? ResultText { get; init; }
    public string? Error { get; init; }
    public required long InputTokens { get; init; }
    public required long OutputTokens { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}
