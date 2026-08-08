using Agw.Shared.Contracts.Tools;

namespace Agw.Tools.ContextualTools;

/// <summary>
/// Materializes one independently selectable Tool using the current Agent and Project context.
/// </summary>
public interface IContextualTool
{
    ToolInfo Descriptor { get; }

    ValueTask<ToolContribution> MaterializeAsync(
        ToolDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken);
}
