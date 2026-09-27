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
                new(AgwFileAccessProvider.ReadFileToolName, AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new(AgwFileAccessProvider.ReadLinesToolName, AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new(AgwFileAccessProvider.LsToolName, AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new(AgwFileAccessProvider.GrepToolName, AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new(AgwFileAccessProvider.WriteToolName, AgwToolPermission.Write),
                new(AgwFileAccessProvider.DeleteFileToolName, AgwToolPermission.Write),
                new(AgwFileAccessProvider.ReplaceToolName, AgwToolPermission.Write),
                new(AgwFileAccessProvider.ReplaceLinesToolName, AgwToolPermission.Write),
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
        contribution.ContextProviders.Add(new AgwFileAccessProvider(_fileSystemResolver, context.ProjectId, snapshot));
        return ValueTask.FromResult(contribution);
    }
}
