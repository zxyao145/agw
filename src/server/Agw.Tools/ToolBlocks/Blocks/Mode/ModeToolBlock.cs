using Microsoft.Agents.AI;

namespace Agw.Tools.ToolBlocks.Blocks.Mode;

public sealed class ModeToolBlock : IToolBlock
{
    public ToolBlockDescriptor Descriptor { get; } = new(
        ToolBlockNames.Mode,
        "Mode",
        "Allows the agent to switch between plan and execute modes.",
        ToolBlockScope.Agent | ToolBlockScope.Project,
        ["mode_set", "mode_get"]);

    public ValueTask<ToolContribution> MaterializeAsync(
        ToolBlockDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken)
    {
        var contribution = new ToolContribution();
        contribution.ContextProviders.Add(new AgentModeProvider(
            new AgentModeProviderOptions
            {
                DefaultMode = context.DefaultMode
            }));
        return ValueTask.FromResult(contribution);
    }
}
