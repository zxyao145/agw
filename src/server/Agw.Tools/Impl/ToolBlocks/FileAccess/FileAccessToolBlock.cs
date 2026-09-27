using Agw.Tools.ToolBlocks;

namespace Agw.Tools.Impl.ToolBlocks.FileAccess;

public sealed class FileAccessToolBlock : IToolBlock
{
    private readonly IAgwFileSystemResolver _fileSystemResolver;

    public FileAccessToolBlock(IAgwFileSystemResolver fileSystemResolver)
    {
        _fileSystemResolver = fileSystemResolver;
    }

    public ToolBlockDescriptor Descriptor { get; } =
        new(
            ToolBlockNames.FileAccess,
            "File Access",
            "Reads and modifies files in the project workspace.",
            ToolBlockScope.Agent | ToolBlockScope.Project,
            [
                .. FileReadonlyAccessToolBlock.Members,
                new(AgwFileAccessProvider.WriteToolName, AgwToolPermission.Write),
                new(AgwFileAccessProvider.DeleteFileToolName, AgwToolPermission.Write),
                new(AgwFileAccessProvider.ReplaceToolName, AgwToolPermission.Write),
                new(AgwFileAccessProvider.ReplaceLinesToolName, AgwToolPermission.Write),
            ],
            requiresWorkspace: true,
            includedToolBlockNames: [ToolBlockNames.FileReadonlyAccess]
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
        contribution.ContextProviders.Add(CreateProvider(_fileSystemResolver, context));
        return ValueTask.FromResult(contribution);
    }

    /// <summary>
    /// Creates the file access provider from the turn's workspace snapshot, or from the Project configuration when no
    /// snapshot was captured.
    /// 使用本轮捕获的工作区快照创建文件访问 Provider；没有快照时按 Project 配置创建。
    /// </summary>
    internal static AgwFileAccessProvider CreateProvider(
        IAgwFileSystemResolver fileSystemResolver,
        ToolMaterializationContext context
    )
    {
        var snapshot =
            context.WorkspaceSnapshot
            ?? ProjectWorkspacePaths.CreateSnapshot(
                context.ProjectId,
                context.Project.Workspace,
                context.Project.AdditionalDirectories.Select(
                    directory => new Agw.Shared.Runtime.ProjectWorkspaceDirectory(directory.Id, directory.Path)
                )
            );
        return new AgwFileAccessProvider(fileSystemResolver, context.ProjectId, snapshot);
    }
}
