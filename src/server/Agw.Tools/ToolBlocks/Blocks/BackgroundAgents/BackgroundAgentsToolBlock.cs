using Agw.Shared.Exceptions;

using Microsoft.Agents.AI;

namespace Agw.Tools.ToolBlocks.Blocks.BackgroundAgents;

public sealed class BackgroundAgentsToolBlock : IToolBlock
{
    public ToolBlockDescriptor Descriptor { get; } = new(
        ToolBlockNames.BackgroundAgents,
        "Background Agents",
        "Delegates work to explicitly allowed agents.",
        ToolBlockScope.Agent,
        [
            "background_agents_start_task",
            "background_agents_wait_for_first_completion",
            "background_agents_get_task_results",
            "background_agents_get_all_tasks",
            "background_agents_continue_task",
            "background_agents_clear_completed_task"
        ]);

    public async ValueTask<ToolContribution> MaterializeAsync(
        ToolBlockDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken)
    {
        if (definition is not BackgroundAgentsToolBlockDefinition { Options: not null } backgroundDefinition)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool Block '{definition.GetDefinitionName()}' does not contain background agent options.");
        }

        var allowedAgentIds = backgroundDefinition.Options.AllowedAgentIds
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var backgroundAgents = context.BackgroundAgents;
        if (backgroundAgents.Count == 0 &&
            context.BackgroundAgentFactory != null &&
            allowedAgentIds.Length > 0)
        {
            backgroundAgents = await context.BackgroundAgentFactory(allowedAgentIds, cancellationToken)
                .ConfigureAwait(false);
        }

        if (backgroundAgents.Count == 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The background-agents Tool Block requires at least one allowed agent.");
        }

        var contribution = new ToolContribution();
        contribution.PlanModeAllowedToolNames.UnionWith(
            ["background_agents_get_task_results", "background_agents_get_all_tasks"]);
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
