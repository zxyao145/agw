using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.ToolBlocks.FileAccess;

/// <summary>
/// Provides only the read tools of an <see cref="AgwFileAccessProvider"/>.
/// 只提供 <see cref="AgwFileAccessProvider"/> 中的读取工具。
/// </summary>
/// <remarks>
/// The exposed tools are the same instances as those in <see cref="AgwFileAccessProvider.Tools"/>, so a read tool name
/// always refers to one implementation. Tool permissions and approval come from the
/// <see cref="FileReadonlyAccessToolBlock"/> member metadata.
/// 暴露的工具就是 <see cref="AgwFileAccessProvider.Tools"/> 中的同一批实例，同一个读取工具名始终对应同一个实现。
/// 工具权限与审批由 <see cref="FileReadonlyAccessToolBlock"/> 的成员元数据决定。
/// </remarks>
internal sealed class AgwFileReadonlyAccessProvider : AIContextProvider
{
    /// <summary>
    /// The read tool names of <see cref="AgwFileAccessProvider"/> that this provider exposes.
    /// 本 Provider 暴露的 <see cref="AgwFileAccessProvider"/> 读取工具名。
    /// </summary>
    public static IReadOnlyList<string> ToolNames { get; } =
    [
        AgwFileAccessProvider.ReadFileToolName,
        AgwFileAccessProvider.ReadLinesToolName,
        AgwFileAccessProvider.LsToolName,
        AgwFileAccessProvider.GrepToolName,
    ];

    private const string Instructions = """
        ## File Read Access
        You can read files in the Project directories via `file_access_read`, `file_access_read_lines`, `file_access_ls`,
        and `file_access_grep`. You cannot create, modify, or delete files with these tools.

        - Files may be organized into subdirectories. Use `file_access_ls` to explore the tree level by level,
          or `file_access_grep` to search file contents recursively across the whole store.
        - To look at part of a file, find the line numbers with `file_access_grep`, then read the range around them
          with `file_access_read_lines`. Reading the whole file first is rarely necessary.
        - File paths are relative to the selected Project directory. Omit `directoryId` for the primary directory;
          use a `directoryId` from the Project directory list for an additional directory.
        """;

    private readonly AgwFileAccessProvider _fileAccessProvider;
    private AITool[]? _tools;

    public AgwFileReadonlyAccessProvider(AgwFileAccessProvider fileAccessProvider)
    {
        _fileAccessProvider = fileAccessProvider;
    }

    public override IReadOnlyList<string> StateKeys => [];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult(
            new AIContext
            {
                Instructions = Instructions,
                Tools = _tools ??=
                    _fileAccessProvider
                        .Tools.Where(static tool => ToolNames.Contains(tool.Name, StringComparer.Ordinal))
                        .ToArray(),
            }
        );
}
