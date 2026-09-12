using Agw.Agents.Definitions.Domain.Decisions;
using Agw.Agents.Definitions.Domain.Policies;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Agents;

public sealed class AgentflowDefinitionValidator
{
    public AgentflowDefinitionDecision Evaluate(
        IReadOnlyList<AgentflowNode>? nodes,
        IReadOnlyList<AgentflowEdge>? edges,
        Guid agentflowId,
        IReadOnlyCollection<Guid> existingAgentIds,
        Guid? summaryModelProviderId = null,
        IReadOnlyCollection<Guid>? existingModelProviderIds = null,
        IReadOnlyDictionary<Guid, string>? existingAgentNames = null,
        IReadOnlyCollection<Guid>? existingAgentflowIds = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>>? nestedReferences = null
    ) =>
        new AgentflowDefinitionPolicy(AgentflowConfigurationParser.Parse(nodes, edges)).Evaluate(
            nodes,
            edges,
            agentflowId,
            existingAgentIds,
            summaryModelProviderId,
            existingModelProviderIds,
            existingAgentNames,
            existingAgentflowIds,
            nestedReferences
        );
}
