using Agw.Tools.Impl.ToolBlocks.Mode;
using Agw.Tools.ToolBlocks;

namespace Agw.Tools.Impl.ToolBlocks.Todo;

public sealed class TodoToolBlock : IToolBlock
{
    public ToolBlockDescriptor Descriptor { get; } =
        new(
            ToolBlockNames.Todo,
            "Todo",
            "Tracks multi-step work with a persistent todo list.",
            ToolBlockScope.Agent | ToolBlockScope.Project,
            [
                new("todos_add", AgwToolPermission.None, allowInPlanMode: true),
                new("todos_complete", AgwToolPermission.None, allowInPlanMode: true),
                new("todos_remove", AgwToolPermission.None, allowInPlanMode: true),
                new("todos_get_remaining", AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new("todos_get_all", AgwToolPermission.ReadOnly, allowInPlanMode: true),
            ]
        );

    public ValueTask<ToolContribution> MaterializeAsync(
        ToolBlockDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken
    )
    {
        var contribution = new ToolContribution();
        contribution.PlanModeAllowedToolNames.UnionWith(
            Descriptor.Members.Where(static member => member.AllowInPlanMode).Select(static member => member.Name)
        );
        var provider = new AgwTodoProvider();
        contribution.ContextProviders.Add(provider);
        contribution.ContextAttachmentSources.Add(
            new TodoReminderAttachmentSource(provider, Descriptor.MemberToolNames)
        );
        var evaluatorOptions = context.EnabledToolBlockNames.Contains(ToolBlockNames.Mode)
            ? new TodoCompletionLoopEvaluatorOptions { Modes = [ModeToolBlock.ExecuteModeName] }
            : null;
        contribution.LoopEvaluators.Add(new TodoCompletionLoopEvaluator(evaluatorOptions));
        return ValueTask.FromResult(contribution);
    }
}
