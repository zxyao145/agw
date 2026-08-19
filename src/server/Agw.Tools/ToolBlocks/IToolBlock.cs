namespace Agw.Tools.ToolBlocks;

public interface IToolBlock
{
    ToolBlockDescriptor Descriptor { get; }

    ValueTask<ToolContribution> MaterializeAsync(
        ToolBlockDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken
    );
}
