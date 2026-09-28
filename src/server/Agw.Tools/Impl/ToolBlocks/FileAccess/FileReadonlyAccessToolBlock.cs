using Agw.Tools.ToolBlocks;

namespace Agw.Tools.Impl.ToolBlocks.FileAccess;

public sealed class FileReadonlyAccessToolBlock : IToolBlock
{
    private readonly IAgwFileSystemResolver _fileSystemResolver;

    public FileReadonlyAccessToolBlock(IAgwFileSystemResolver fileSystemResolver)
    {
        _fileSystemResolver = fileSystemResolver;
    }

    /// <summary>
    /// The read tool declarations, shared with <see cref="FileAccessToolBlock"/> which includes this block.
    /// 读取工具的成员声明，与包含本 Block 的 <see cref="FileAccessToolBlock"/> 共用。
    /// </summary>
    internal static IReadOnlyList<ToolBlockMemberDescriptor> Members { get; } =
        AgwFileReadonlyAccessProvider
            .ToolNames.Select(static name => new ToolBlockMemberDescriptor(
                name,
                AgwToolPermission.ReadOnly,
                allowInPlanMode: true
            ))
            .ToArray();

    public ToolBlockDescriptor Descriptor { get; } =
        new(
            ToolBlockNames.FileReadonlyAccess,
            "File Readonly Access",
            "Reads and searches files in the project workspace.",
            ToolBlockScope.Agent | ToolBlockScope.Project,
            Members,
            requiresWorkspace: true
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
        contribution.ContextProviders.Add(
            new AgwFileReadonlyAccessProvider(FileAccessToolBlock.CreateProvider(_fileSystemResolver, context))
        );
        return ValueTask.FromResult(contribution);
    }
}
