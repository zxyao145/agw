using Agw.Agents.Application.Persistence;
using Agw.Agents.ExternalAgents.Codex;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using OpenAI.CodexSdk.AppServer;
using OpenAI.CodexSdk.AppServer.Protocol;
using OpenAI.CodexSdk.AppServer.Protocol.V1;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Codex;

internal sealed class CodexImagePathReader : ICodexImagePathReader
{
    private const int MaximumItemIdsPerRequest = 100;
    private readonly IAgentsDbContext _dbContext;

    public CodexImagePathReader(IAgentsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        Guid? agentId,
        string threadId,
        string turnId,
        IReadOnlyList<string> itemIds,
        CancellationToken cancellationToken
    )
    {
        var requestedItemIds = itemIds.Distinct(StringComparer.Ordinal).ToArray();
        Dictionary<string, string> imagePaths = new(StringComparer.Ordinal);
        if (requestedItemIds.Length == 0)
            return imagePaths;

        string? agentExtra = null;
        if (agentId.HasValue)
        {
            var agent = await _dbContext
                .Agents.AsNoTracking()
                .Where(agent => agent.Id == agentId.Value)
                .Select(agent => new { agent.Extra })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (agent is null)
                throw new AgwException(ErrorCodes.ResourceNotFound);
            agentExtra = agent.Extra;
        }
        var executablePath = string.IsNullOrWhiteSpace(agentExtra)
            ? "codex"
            : CodexAgentSettings.Deserialize(agentExtra)?.CodexOptions.CodexPathOverride ?? "codex";

        await using var client = await CodexAppServerClient
            .StartAsync(
                new CodexAppServerClientOptions
                {
                    ExecutablePath = executablePath,
                    Capabilities = new InitializeCapabilities { ExperimentalApi = true },
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        foreach (var batch in requestedItemIds.Chunk(MaximumItemIdsPerRequest))
        {
            var batchItemIds = batch.ToHashSet(StringComparer.Ordinal);
            var response = await client
                .ThreadItemsReadAsync(
                    new ThreadItemsReadParams
                    {
                        ThreadId = threadId,
                        TurnId = turnId,
                        ItemIds = batch,
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);

            foreach (var entry in response.Data)
            {
                if (
                    string.Equals(entry.TurnId, turnId, StringComparison.Ordinal)
                    && batchItemIds.Contains(entry.Item.Id)
                    && entry.Item is ImageViewThreadItem imageView
                )
                    imagePaths[imageView.Id] = imageView.Path;
            }
        }

        return imagePaths;
    }
}
