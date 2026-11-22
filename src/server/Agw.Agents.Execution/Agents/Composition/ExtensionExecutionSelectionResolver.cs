using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Auth.Contracts;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Tooling;

namespace Agw.Agents.Execution.Agents.Composition;

internal interface IExtensionExecutionSelectionResolver
{
    Task<ExtensionCapabilitySelection> ResolveAsync(
        ExecutionTarget target,
        AgentflowRevisionSnapshot? snapshot,
        ProjectRuntimeSnapshot project,
        CancellationToken cancellationToken
    );
}

internal sealed class ExtensionExecutionSelectionResolver : IExtensionExecutionSelectionResolver
{
    private readonly AgentAppService _agents;
    private readonly IAgentflowDefinitionReader _agentflows;
    private readonly IExtensionCapabilityCatalog _extensionCapabilities;

    public ExtensionExecutionSelectionResolver(
        AgentAppService agents,
        IAgentflowDefinitionReader agentflows,
        IExtensionCapabilityCatalog extensionCapabilities
    )
    {
        _agents = agents;
        _agentflows = agentflows;
        _extensionCapabilities = extensionCapabilities;
    }

    public async Task<ExtensionCapabilitySelection> ResolveAsync(
        ExecutionTarget target,
        AgentflowRevisionSnapshot? snapshot,
        ProjectRuntimeSnapshot project,
        CancellationToken cancellationToken
    )
    {
        var toolSelections = new HashSet<ExtensionToolSelection>();
        var usesProjectCapabilities = false;

        if (target.AgentType == AgentRuntimeType.Agent)
        {
            var agent =
                await _agents.GetAgentAsync(target.AgentId).ConfigureAwait(false)
                ?? throw new AgwException(ErrorCodes.ResourceNotFound, "The Agent was not found.");
            if (agent.Type != AgentType.External)
            {
                usesProjectCapabilities = true;
                await CollectAgentAsync(agent, toolSelections, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            var acceptedSnapshot = snapshot ?? throw new AgwException(ErrorCodes.DurableExecutionConflict);
            await VisitAgentflowAsync(
                    target.AgentId,
                    acceptedSnapshot.RootRevision,
                    acceptedSnapshot,
                    new HashSet<(Guid AgentflowId, int Revision)>(),
                    new HashSet<(Guid AgentflowId, int Revision)>(),
                    toolSelections,
                    () => usesProjectCapabilities = true,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        if (usesProjectCapabilities)
        {
            CollectToolSelections(project.Tools, toolSelections);
        }

        IReadOnlyList<Guid> skillExtensionIds = usesProjectCapabilities
            ? await _extensionCapabilities.ListEnabledSkillExtensionIdsAsync(cancellationToken).ConfigureAwait(false)
            : [];
        return new ExtensionCapabilitySelection(toolSelections.ToArray(), skillExtensionIds);
    }

    private async Task VisitAgentflowAsync(
        Guid agentflowId,
        int agentflowRevision,
        AgentflowRevisionSnapshot snapshot,
        HashSet<(Guid AgentflowId, int Revision)> visited,
        HashSet<(Guid AgentflowId, int Revision)> path,
        HashSet<ExtensionToolSelection> toolSelections,
        Action markProjectCapabilitiesUsed,
        CancellationToken cancellationToken
    )
    {
        var key = (agentflowId, agentflowRevision);
        if (path.Contains(key))
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"Nested Agentflow cycle at '{agentflowId}'.");
        }
        if (!visited.Add(key))
        {
            return;
        }

        var flow = await _agentflows
            .FindVisibleAsync(agentflowId, UserInfoUtil.RequiredUserId, cancellationToken)
            .ConfigureAwait(false);
        if (flow == null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound, "The Agentflow was not found.");
        }

        path.Add(key);
        var nodes = await _agentflows
            .ListNodesAsync(agentflowId, agentflowRevision, cancellationToken)
            .ConfigureAwait(false);
        var members = await _agentflows
            .ListBlockMembersAsync(agentflowId, agentflowRevision, cancellationToken)
            .ConfigureAwait(false);
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Kind == AgentflowNodeKind.Agent && node.RelateId is { } agentId)
            {
                var agent =
                    await _agents.GetAgentAsync(agentId).ConfigureAwait(false)
                    ?? throw new AgwException(ErrorCodes.ResourceNotFound, "An Agent in the Agentflow was not found.");
                if (agent.Type != AgentType.External)
                {
                    markProjectCapabilitiesUsed();
                    await CollectAgentAsync(agent, toolSelections, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (node.Kind == AgentflowNodeKind.WorkflowAsAgent && node.RelateId is { } nestedAgentflowId)
            {
                await VisitAgentflowAsync(
                        nestedAgentflowId,
                        snapshot.GetTargetRevision(agentflowId, agentflowRevision, node.NodeId, blockMemberId: null),
                        snapshot,
                        visited,
                        path,
                        toolSelections,
                        markProjectCapabilitiesUsed,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }

        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member.Kind == AgentflowNodeKind.Agent)
            {
                var agent =
                    await _agents.GetAgentAsync(member.RelateId).ConfigureAwait(false)
                    ?? throw new AgwException(ErrorCodes.ResourceNotFound, "An Agent in the Agentflow was not found.");
                if (agent.Type != AgentType.External)
                {
                    markProjectCapabilitiesUsed();
                    await CollectAgentAsync(agent, toolSelections, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (member.Kind == AgentflowNodeKind.WorkflowAsAgent)
            {
                await VisitAgentflowAsync(
                        member.RelateId,
                        snapshot.GetTargetRevision(agentflowId, agentflowRevision, member.NodeId, member.MemberId),
                        snapshot,
                        visited,
                        path,
                        toolSelections,
                        markProjectCapabilitiesUsed,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }

        path.Remove(key);
    }

    private async Task CollectAgentAsync(
        Agent agent,
        ISet<ExtensionToolSelection> toolSelections,
        CancellationToken cancellationToken
    )
    {
        CollectToolSelections(agent.Tools, toolSelections);
        var backgroundAgentIds = agent
            .Tools.OfType<ToolBlockValue>()
            .Select(value => value.Definition)
            .OfType<BackgroundAgentsToolBlockDefinition>()
            .SelectMany(definition => definition.Options.AllowedAgentIds)
            .Where(id => id != agent.Id)
            .Distinct();
        foreach (var backgroundAgentId in backgroundAgentIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var backgroundAgent = await _agents.GetAgentForCurrentUserAsync(backgroundAgentId).ConfigureAwait(false);
            if (backgroundAgent?.Type == AgentType.System)
                CollectToolSelections(backgroundAgent.Tools, toolSelections);
        }
    }

    private static void CollectToolSelections(
        IReadOnlyList<ToolValueObject> values,
        ISet<ExtensionToolSelection> toolSelections
    )
    {
        foreach (var definition in values.OfType<ToolValue>().Select(value => value.Definition))
        {
            if (definition is ExtensionToolDefinition extension)
            {
                toolSelections.Add(new ExtensionToolSelection(extension.ExtensionId, extension.ToolId));
            }
        }
    }
}
