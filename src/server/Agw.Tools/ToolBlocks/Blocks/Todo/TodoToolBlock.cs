using Microsoft.Agents.AI;

namespace Agw.Tools.ToolBlocks.Blocks.Todo;

public sealed class TodoToolBlock : IToolBlock
{
    public ToolBlockDescriptor Descriptor { get; } = new(
        ToolBlockNames.Todo,
        "Todo",
        "Tracks multi-step work with a persistent todo list.",
        ToolBlockScope.Agent | ToolBlockScope.Project,
        ["todos_add", "todos_complete", "todos_remove", "todos_get_remaining", "todos_get_all"]);

    public ValueTask<ToolContribution> MaterializeAsync(
        ToolBlockDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken)
    {
        var contribution = new ToolContribution();
        contribution.PlanModeAllowedToolNames.UnionWith(Descriptor.MemberToolNames);
        contribution.ContextProviders.Add(new TodoProvider());
        var evaluatorOptions = context.EnabledToolBlockNames.Contains(ToolBlockNames.Mode)
            ? new TodoCompletionLoopEvaluatorOptions
            {
                Modes = ["execute"]
            }
            : null;
        contribution.LoopEvaluators.Add(new TodoCompletionLoopEvaluator(evaluatorOptions));
        return ValueTask.FromResult(contribution);
    }
}
