using Agw.Files.Abstracts;
using Agw.Tools.ToolBlocks.Storage;

using Microsoft.Agents.AI;

namespace Agw.Tools.ToolBlocks.Blocks.FileAccess;

public sealed class FileAccessToolBlock : IToolBlock
{
    private readonly IAgwFileSystemResolver _fileSystemResolver;

    public FileAccessToolBlock(IAgwFileSystemResolver fileSystemResolver)
    {
        _fileSystemResolver = fileSystemResolver;
    }

    public ToolBlockDescriptor Descriptor { get; } = new(
        ToolBlockNames.FileAccess,
        "File Access",
        "Reads and modifies files in the project workspace.",
        ToolBlockScope.Agent | ToolBlockScope.Project,
        [
            "file_access_read",
            "file_access_ls",
            "file_access_grep",
            "file_access_write",
            "file_access_delete",
            "file_access_replace",
            "file_access_replace_lines"
        ],
        requiresWorkspace: true,
        mayRequireApproval: true);

    public ValueTask<ToolContribution> MaterializeAsync(
        ToolBlockDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken)
    {
        var contribution = new ToolContribution();
        contribution.ContextProviders.Add(new FileAccessProvider(
            new ProjectAgentFileStore(_fileSystemResolver, context.ProjectId)));
        contribution.AutoApprovalRules.Add(FileAccessProvider.ReadOnlyToolsAutoApprovalRule);
        return ValueTask.FromResult(contribution);
    }
}
