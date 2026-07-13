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
        CancellationToken cancellationToken = default);
}
