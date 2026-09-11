using Agw.Tools.Abstractions.Generated;
using Agw.Tools.ToolBlocks;

namespace Agw.Tools.Generated;

internal sealed class GeneratedToolBlock : IToolBlock
{
    private readonly AgwGeneratedToolCatalog _catalog;
    private readonly IReadOnlyList<AgwGeneratedToolDescriptor> _members;

    public GeneratedToolBlock(AgwGeneratedToolBlockDescriptor descriptor, AgwGeneratedToolCatalog catalog)
    {
        _catalog = catalog;
        _members = descriptor.MemberToolNames.Select(name => catalog.GetTool(name)!).ToArray();
        Descriptor = new ToolBlockDescriptor(
            descriptor.Name,
            descriptor.DisplayName,
            descriptor.Description,
            descriptor.Scopes,
            _members
                .Select(static member => new ToolBlockMemberDescriptor(
                    member.Name,
                    member.RequiredPermission,
                    member.AllowInPlanMode,
                    member.Category
                ))
                .ToArray(),
            requiresWorkspace: _members.Any(static member => member.RequiresWorkspace),
            excludeFromList: descriptor.ExcludeFromList
        );
    }

    public ToolBlockDescriptor Descriptor { get; }

    public ValueTask<ToolContribution> MaterializeAsync(
        ToolBlockDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken
    )
    {
        var contribution = new ToolContribution();
        foreach (AgwGeneratedToolDescriptor member in _members)
        {
            contribution.Tools.Add(_catalog.Create(member, context.ProjectId));
        }
        return ValueTask.FromResult(contribution);
    }
}
