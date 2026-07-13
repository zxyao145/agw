using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;

namespace Agw.Agents.Definitions.Domain;

public class AgentDomainService
{
    private readonly TimeProvider _timeProvider;

    public AgentDomainService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public void PrepareForCreate(Agent agent, string user)
    {
        ArgumentNullException.ThrowIfNull(agent);

        EnsureModelProviderIsPresentWhenRequired(agent);
        agent.Id = agent.Id == Guid.Empty ? Guid.NewGuid() : agent.Id;
        agent.Name = string.IsNullOrWhiteSpace(agent.Name) ? agent.Id.Normalize() : agent.Name;
        agent.CreateBy = user;
        agent.CreateTime = _timeProvider.GetUtcNow();
    }

    public void ApplyUpdate(Agent existing, Action<Agent> updateAction, string user)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(updateAction);

        if (existing.Type == AgentType.External)
        {
            var originalId = existing.Id;
            var originalName = existing.Name;
            var originalSystemPrompt = existing.SystemPrompt;
            var originalTools = existing.Tools;
            var originalType = existing.Type;

            updateAction(existing);

            existing.Id = originalId;
            existing.Name = originalName;
            existing.SystemPrompt = originalSystemPrompt;
            existing.Tools = originalTools;
            existing.Type = originalType;
        }
        else
        {
            updateAction(existing);
        }

        EnsureModelProviderIsPresentWhenRequired(existing);
        existing.Name = string.IsNullOrWhiteSpace(existing.Name) ? existing.Id.Normalize() : existing.Name;
        existing.UpdateBy = user;
        existing.UpdateTime = _timeProvider.GetUtcNow();
    }

    public IReadOnlyList<Guid> NormalizeMcpToolServerIds(IEnumerable<Guid>? mcpToolServerIds)
    {
        return (mcpToolServerIds ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
    }

    private static void EnsureModelProviderIsPresentWhenRequired(Agent agent)
    {
        if (agent.Type == AgentType.System && !agent.ModelProviderId.HasValue)
        {
            throw new AgwException(ErrorCodes.SystemAgentRequiresModelProvider, "System agents must have a ModelProviderId.");
        }
    }
}
