using Agw.Shared.Exceptions;
using Agw.Tools.ToolBlocks;
using Microsoft.Agents.AI;

namespace Agw.Tools.Impl.ToolBlocks.BackgroundAgents;

public sealed class BackgroundAgentsToolBlock : IToolBlock
{
    public ToolBlockDescriptor Descriptor { get; } =
        new(
            ToolBlockNames.BackgroundAgents,
            "Background Agents",
            "Delegates work to explicitly allowed agents.",
            ToolBlockScope.Agent,
            [
                new("background_agents_start_task", AgwToolPermission.ReadOnly),
                new("background_agents_wait_for_first_completion", AgwToolPermission.ReadOnly),
                new("background_agents_get_task_results", AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new("background_agents_get_all_tasks", AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new("background_agents_continue_task", AgwToolPermission.ReadOnly),
                new("background_agents_clear_completed_task", AgwToolPermission.ReadOnly),
            ]
        );

    public async ValueTask<ToolContribution> MaterializeAsync(
        ToolBlockDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken
    )
    {
        if (definition is not BackgroundAgentsToolBlockDefinition { Options: not null } backgroundDefinition)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool Block '{definition.GetDefinitionName()}' does not contain background agent options."
            );
        }

        var allowedAgentIds = backgroundDefinition
            .Options.AllowedAgentIds.Where(static id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var backgroundAgents = context.BackgroundAgents;
        if (backgroundAgents.Count == 0 && context.BackgroundAgentFactory != null && allowedAgentIds.Length > 0)
        {
            backgroundAgents = await context
                .BackgroundAgentFactory(allowedAgentIds, cancellationToken)
                .ConfigureAwait(false);
        }

        if (backgroundAgents.Count == 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The background-agents Tool Block requires at least one allowed agent."
            );
        }

        var contribution = new ToolContribution();
        contribution.PlanModeAllowedToolNames.UnionWith(
            Descriptor.Members.Where(static member => member.AllowInPlanMode).Select(static member => member.Name)
        );
        contribution.ContextProviders.Add(new BackgroundAgentsProvider(backgroundAgents));
        foreach (var agent in backgroundAgents)
        {
            contribution.AddResource(new AgentResource(agent));
        }

        return contribution;
    }

    private sealed class AgentResource : IAsyncDisposable
    {
        private readonly AIAgent _agent;

        public AgentResource(AIAgent agent)
        {
            _agent = agent;
        }

        public async ValueTask DisposeAsync()
        {
            if (_agent is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (_agent is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
