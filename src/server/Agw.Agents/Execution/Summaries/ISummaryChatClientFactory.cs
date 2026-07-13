using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Summaries;

public interface ISummaryChatClientFactory
{
    Task<IChatClient?> CreateAsync(
        Guid modelProviderId,
        CancellationToken cancellationToken = default);
}
