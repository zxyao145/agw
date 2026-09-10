namespace Agw.Tools.Contracts.Abstractions;

/// <summary>Materializes one independently selectable Tool using the current Agent and Project context.</summary>
public interface IContextualTool : IAgwToolMeta
{
    ValueTask<ToolContribution> MaterializeAsync(
        ToolDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken
    );
}
