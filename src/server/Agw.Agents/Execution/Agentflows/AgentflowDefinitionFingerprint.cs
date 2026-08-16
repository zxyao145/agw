using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Agw.Shared.Data.Entities.Agentflows;

using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Execution.Agentflows;

internal static class AgentflowDefinitionFingerprint
{
    private const int CheckpointRuntimeVersion = 2;

    public static async Task<string?> CreateAsync(
        DbContext dbContext,
        Guid agentflowId,
        CancellationToken cancellationToken)
    {
        var agentflow = await dbContext.Set<Agentflow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == agentflowId, cancellationToken)
            .ConfigureAwait(false);
        if (agentflow == null)
        {
            return null;
        }

        var nodes = await dbContext.Set<AgentflowNode>()
            .AsNoTracking()
            .Where(item => item.AgentflowId == agentflowId)
            .OrderBy(item => item.NodeId)
            .Select(item => new
            {
                item.NodeId,
                item.Kind,
                item.RelateId,
                item.Name,
                item.Instructions,
                item.ConfigJson
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var edges = await dbContext.Set<AgentflowEdge>()
            .AsNoTracking()
            .Where(item => item.AgentflowId == agentflowId)
            .OrderBy(item => item.EdgeId)
            .Select(item => new
            {
                item.EdgeId,
                item.SourceNodeId,
                item.TargetNodeId,
                item.Kind,
                item.Label,
                item.ConditionJson,
                item.ConfigJson
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var definition = JsonSerializer.Serialize(new
        {
            CheckpointRuntimeVersion,
            agentflow.Id,
            agentflow.SystemPrompt,
            agentflow.SummaryModelProviderId,
            Nodes = nodes,
            Edges = edges
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition)))
            .ToLowerInvariant();
    }
}
