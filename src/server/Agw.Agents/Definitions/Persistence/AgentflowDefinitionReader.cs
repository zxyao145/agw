using Agw.Agents.Application.Persistence;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Persistence;

internal sealed class AgentflowDefinitionReader : IAgentflowDefinitionReader
{
    private readonly IAgentsDbContext _dbContext;

    public AgentflowDefinitionReader(IAgentsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Agentflow?> FindVisibleAsync(
        Guid agentflowId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    ) =>
        _dbContext
            .Agentflows.AsNoTracking()
            .FirstOrDefaultAsync(
                agentflow => agentflow.Id == agentflowId && agentflow.CreateBy == ownerUserId,
                cancellationToken
            );

    public async Task<IReadOnlyList<AgentflowNode>> ListNodesAsync(
        Guid agentflowId,
        int agentflowRevision,
        CancellationToken cancellationToken = default
    ) =>
        await _dbContext
            .AgentflowNodes.AsNoTracking()
            .Where(node => node.AgentflowId == agentflowId && node.AgentflowRevision == agentflowRevision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<AgentflowEdge>> ListEdgesAsync(
        Guid agentflowId,
        int agentflowRevision,
        CancellationToken cancellationToken = default
    ) =>
        await _dbContext
            .AgentflowEdges.AsNoTracking()
            .Where(edge => edge.AgentflowId == agentflowId && edge.AgentflowRevision == agentflowRevision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<AgentflowBlockMember>> ListBlockMembersAsync(
        Guid agentflowId,
        int agentflowRevision,
        CancellationToken cancellationToken = default
    ) =>
        await _dbContext
            .AgentflowBlockMembers.AsNoTracking()
            .Where(member => member.AgentflowId == agentflowId && member.AgentflowRevision == agentflowRevision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
