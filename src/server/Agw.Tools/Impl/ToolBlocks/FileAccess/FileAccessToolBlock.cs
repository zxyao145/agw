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
                new("file_access_read", AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new("file_access_read_lines", AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new("file_access_ls", AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new("file_access_grep", AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new("file_access_write", AgwToolPermission.Write),
                new("file_access_delete", AgwToolPermission.Write),
                new("file_access_replace", AgwToolPermission.Write),
                new("file_access_replace_lines", AgwToolPermission.Write),
            ],
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
        var snapshot =
            context.WorkspaceSnapshot
            ?? ProjectWorkspacePaths.CreateSnapshot(
                context.ProjectId,
                context.Project.Workspace,
                context.Project.AdditionalDirectories.Select(
                    directory => new Agw.Shared.Runtime.ProjectWorkspaceDirectory(directory.Id, directory.Path)
                )
            );
        var provider = new DirectoryFileAccessProvider(_fileSystemResolver, context.ProjectId, snapshot);
        contribution.ContextProviders.Add(provider);
        contribution.AddResource(provider);
        return ValueTask.FromResult(contribution);
    }
}
