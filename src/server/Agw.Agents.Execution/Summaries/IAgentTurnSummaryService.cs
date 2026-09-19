using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Summaries;

public interface IAgentTurnSummaryService
{
    Task<ChatMessage> CreateResultAsync(
        Guid modelProviderId,
        IReadOnlyList<ChatMessage> sourceMessages,
        Guid projectId,
        string contextId,
        string? customInstructions,
        CancellationToken cancellationToken = default
    );
}

public interface IAgentStructuredResultService
{
    Task<ChatMessage> CreateStructuredResultAsync(
        string finalText,
        Guid projectId,
        string contextId,
        CancellationToken cancellationToken = default
    );
}
