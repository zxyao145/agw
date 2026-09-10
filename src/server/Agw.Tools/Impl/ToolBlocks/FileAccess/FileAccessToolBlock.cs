using Agw.Files.Abstracts;
using Agw.Tools.Impl.ToolBlocks.Storage;
using Agw.Tools.ToolBlocks;
using Microsoft.Agents.AI;

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
        contribution.ContextProviders.Add(
            new FileAccessProvider(
                new ProjectAgentFileStore(_fileSystemResolver, context.ProjectId),
                // SDK 默认连只读函数也要求审批。由 Registry 按 Members 的权限统一添加审批包装，
                // 避免 SDK 的默认标记残留在 ReadOnly 工具内部，同时保留 Write 成员的审批。
                new FileAccessProviderOptions { DisableReadOnlyToolApproval = true, DisableWriteToolApproval = true }
            )
        );
        return ValueTask.FromResult(contribution);
    }
}
