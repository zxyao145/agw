using System.Text;
using Agw.Files.Abstracts.Dtos;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Tools.Impl.ToolBlocks.Storage;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Agw.Tools.Impl.ToolBlocks.FileAccess;

/// <summary>
/// Provides file access tools over the primary and additional directories of a captured Project workspace.
/// 基于已捕获的 Project 工作区快照，为主目录和附加目录提供文件访问工具。
/// </summary>
/// <remarks>
/// Every tool accepts an optional <c>directoryId</c>: omitted or <see langword="null"/> selects the primary directory,
/// otherwise it must be the ID of an additional directory in the snapshot. File operations, path validation, and write
/// serialization happen in <see cref="IAgwFileSystem"/>; this provider defines the tools and formats their results.
/// Tool permissions and approval come from the <see cref="FileAccessToolBlock"/> member metadata.
/// 每个工具都接受可选的 <c>directoryId</c>：省略或为 <see langword="null"/> 时使用主目录，否则必须是快照中某个附加目录的 ID。
/// 文件操作、路径校验和写入互斥都在 <see cref="IAgwFileSystem"/> 中完成，本类只定义工具并整理结果。
/// 工具权限与审批由 <see cref="FileAccessToolBlock"/> 的成员元数据决定。
/// </remarks>
internal sealed class AgwFileAccessProvider : AIContextProvider
{
    public const string WriteToolName = "file_access_write";
    public const string ReadFileToolName = "file_access_read";
    public const string ReadLinesToolName = "file_access_read_lines";
    public const string DeleteFileToolName = "file_access_delete";
    public const string LsToolName = "file_access_ls";
    public const string GrepToolName = "file_access_grep";
    public const string ReplaceToolName = "file_access_replace";
    public const string ReplaceLinesToolName = "file_access_replace_lines";

    private const string DirectoryIdDescription =
        "Additional Project directory ID. Omit or use null for the primary directory.";

    private const string Instructions = """
        ## File Access
        You have access to a shared file storage area via the `file_access_*` tools for reading, writing, and managing files.
        These files persist beyond the current session and may be shared across sessions or agents.
        Use these tools to read input data provided by the user, write output artifacts, and manage any files the user has asked you to work with.

        - Never delete or overwrite existing files unless the user has explicitly asked you to do so.
        - Files may be organized into subdirectories. Use `file_access_ls` to explore the tree level by level,
          or `file_access_grep` to search file contents recursively across the whole store.
        - To make small edits to an existing file, prefer `file_access_replace` (substring replacement) or
          `file_access_replace_lines` (whole-line replacement) over rewriting the whole file.
        - To change part of a file, find the line numbers with `file_access_grep`, read the range around them
          with `file_access_read_lines`, then edit with `file_access_replace_lines`. Reading the whole file
          first is rarely necessary.
        - File paths are relative to the selected Project directory. Omit `directoryId` for the primary directory;
          use a `directoryId` from the Project directory list for an additional directory.
        """;

    private readonly ProjectAgentFileStore _primaryStore;
    private readonly Dictionary<Guid, ProjectAgentFileStore> _additionalStores;
    private AITool[]? _tools;

    public AgwFileAccessProvider(IAgwFileSystemResolver resolver, Guid projectId, ProjectWorkspaceSnapshot snapshot)
    {
        _primaryStore = new ProjectAgentFileStore(resolver, projectId, null, snapshot);
        _additionalStores = snapshot.AdditionalDirectories.ToDictionary(
            static directory => directory.Id,
            directory => new ProjectAgentFileStore(resolver, projectId, null, snapshot, directory.Id)
        );
    }

    public override IReadOnlyList<string> StateKeys => [];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult(new AIContext { Instructions = Instructions, Tools = _tools ??= CreateTools() });

    [Description(
        "Write a file with the given name and content. By default, does not overwrite an existing file unless overwrite is set to true."
    )]
    private async Task<string> WriteAsync(
        string fileName,
        string content,
        bool overwrite = false,
        [Description(DirectoryIdDescription)] string? directoryId = null,
        CancellationToken cancellationToken = default
    )
    {
        var store = ResolveStore(directoryId);
        if (overwrite)
        {
            await store.WriteAsync(fileName, content, cancellationToken).ConfigureAwait(false);
        }
        else if (!await store.CreateFileAsync(fileName, content, cancellationToken).ConfigureAwait(false))
        {
            return $"File '{fileName}' already exists. To replace it, write again with overwrite set to true.";
        }

        return $"File '{fileName}' written.";
    }

    [Description(
        "Read the content of a file by name. Returns the file content or a message indicating the file was not found. To edit by 1-based line number afterwards, count lines terminated by \\n, \\r\\n, or a lone \\r; each line keeps its own terminator, and content ending in a terminator has no extra empty line after it."
    )]
    private async Task<string> ReadAsync(
        string fileName,
        [Description(DirectoryIdDescription)] string? directoryId = null,
        CancellationToken cancellationToken = default
    )
    {
        var content = await ResolveStore(directoryId).ReadAsync(fileName, cancellationToken).ConfigureAwait(false);
        return content ?? $"File '{fileName}' not found.";
    }

    [Description(
        "Read part of a file by 1-based inclusive line number; omit endLine to read to the end of the file, and an endLine past the last line is clamped. Each line is prefixed with its number and a tab; everything after that tab is verbatim, including the line's own terminator, so it can be reused as a file_access_replace_lines new_line. Line numbers are 1-based and count lines terminated by \\n, \\r\\n, or a lone \\r, and content ending in a terminator has no extra empty line after it."
    )]
    private async Task<string> ReadLinesAsync(
        string fileName,
        int startLine,
        int? endLine = null,
        [Description(DirectoryIdDescription)] string? directoryId = null,
        CancellationToken cancellationToken = default
    )
    {
        var lines = await ResolveStore(directoryId)
            .ReadLinesAsync(fileName, startLine, endLine, cancellationToken)
            .ConfigureAwait(false);
        if (lines == null)
        {
            return $"File '{fileName}' not found.";
        }

        // 每行保留自己的结束符，因此结束符同时充当行分隔符。
        // Each line keeps its terminator, so it doubles as the row separator.
        var builder = new StringBuilder();
        for (var index = 0; index < lines.Count; index++)
        {
            builder.Append(startLine + index).Append('\t').Append(lines[index]);
        }

        return builder.ToString();
    }

    [Description("Delete a file by name.")]
    private async Task<string> DeleteAsync(
        string fileName,
        [Description(DirectoryIdDescription)] string? directoryId = null,
        CancellationToken cancellationToken = default
    )
    {
        var deleted = await ResolveStore(directoryId).DeleteAsync(fileName, cancellationToken).ConfigureAwait(false);
        return deleted ? $"File '{fileName}' deleted." : $"File '{fileName}' not found.";
    }

    [Description(
        "List the direct child files and subdirectories of a directory. Omit the directory (or pass an empty string) to list the root. To enumerate a subdirectory, pass its relative path, for example \"reports\" or \"reports/2024\". Optionally filter entries with a globPattern (e.g. \"*.md\"). Subdirectories are listed before files, and each entry has a name and a type of \"file\" or \"directory\"."
    )]
    private async Task<List<AgwFileStoreEntry>> LsAsync(
        string? directory = null,
        string? globPattern = null,
        [Description(DirectoryIdDescription)] string? directoryId = null,
        CancellationToken cancellationToken = default
    )
    {
        var target = string.IsNullOrWhiteSpace(directory) ? string.Empty : directory;
        var entries = await ResolveStore(directoryId)
            .ListChildrenAsync(target, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(globPattern))
        {
            return [.. entries];
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(globPattern);
        return entries.Where(entry => matcher.Match(entry.Name).HasMatches).ToList();
    }

    [Description(
        "Replace occurrences of oldString with newString in a file. Fails if oldString is not found, or if it occurs more than once and replaceAll is false. Returns the number of occurrences replaced."
    )]
    private async Task<string> ReplaceAsync(
        string fileName,
        string oldString,
        string newString,
        bool replaceAll = false,
        [Description(DirectoryIdDescription)] string? directoryId = null,
        CancellationToken cancellationToken = default
    )
    {
        var count = await ResolveStore(directoryId)
            .ReplaceTextAsync(fileName, oldString, newString, replaceAll, cancellationToken)
            .ConfigureAwait(false);
        return count == null ? $"File '{fileName}' not found." : $"Replaced {count} occurrence(s) in '{fileName}'.";
    }

    [Description(
        "Replace lines in a file. Provide a list of edits, each with a 1-based line_number and a literal new_line (include your own trailing newline); an empty new_line deletes the line, including its line break. Fails on out-of-range or duplicate line numbers. Line numbers are 1-based and count lines terminated by \\n, \\r\\n, or a lone \\r; each line keeps its own terminator, and content ending in a terminator has no extra empty line after it."
    )]
    private async Task<string> ReplaceLinesAsync(
        string fileName,
        List<AgwFileLineEdit> edits,
        [Description(DirectoryIdDescription)] string? directoryId = null,
        CancellationToken cancellationToken = default
    )
    {
        var replaced = await ResolveStore(directoryId)
            .ReplaceLinesAsync(fileName, edits, cancellationToken)
            .ConfigureAwait(false);
        return replaced ? $"Replaced {edits.Count} line(s) in '{fileName}'." : $"File '{fileName}' not found.";
    }

    [Description(
        """
            Search the contents of files in the store (recursively, across all subdirectories) using a regular expression pattern (case-insensitive).
            Optionally restrict the search to a base directory (relative path), and filter which files to search using a glob pattern matched against each file's path relative to that directory:
            - '*' matches within a single path segment
            - '**' matches across subdirectories, so use "**/*.md" to match markdown files at any depth, or "reports/**" to restrict the search to the 'reports' subtree.

            Returns matching results whose file names are paths relative to the store root (usable with file_access_read), along with snippets and matching lines with line numbers.
            Line numbers are 1-based and count lines terminated by \n, \r\n, or a lone \r, and content ending in a terminator has no extra empty line after it.
            """
    )]
    private async Task<List<AgwFileSearchResult>> GrepAsync(
        string regexPattern,
        string? globPattern = null,
        string? directory = null,
        [Description(DirectoryIdDescription)] string? directoryId = null,
        CancellationToken cancellationToken = default
    )
    {
        var results = await ResolveStore(directoryId)
            .SearchAsync(
                directory ?? string.Empty,
                regexPattern,
                string.IsNullOrWhiteSpace(globPattern) ? null : globPattern,
                recursive: true,
                cancellationToken
            )
            .ConfigureAwait(false);
        return [.. results];
    }

    private AITool[] CreateTools()
    {
        var serializerOptions = AIJsonUtilities.DefaultOptions;
        return
        [
            AIFunctionFactory.Create(
                ReadAsync,
                new AIFunctionFactoryOptions { Name = ReadFileToolName, SerializerOptions = serializerOptions }
            ),
            AIFunctionFactory.Create(
                ReadLinesAsync,
                new AIFunctionFactoryOptions { Name = ReadLinesToolName, SerializerOptions = serializerOptions }
            ),
            AIFunctionFactory.Create(
                LsAsync,
                new AIFunctionFactoryOptions { Name = LsToolName, SerializerOptions = serializerOptions }
            ),
            AIFunctionFactory.Create(
                GrepAsync,
                new AIFunctionFactoryOptions { Name = GrepToolName, SerializerOptions = serializerOptions }
            ),
            AIFunctionFactory.Create(
                WriteAsync,
                new AIFunctionFactoryOptions { Name = WriteToolName, SerializerOptions = serializerOptions }
            ),
            AIFunctionFactory.Create(
                DeleteAsync,
                new AIFunctionFactoryOptions { Name = DeleteFileToolName, SerializerOptions = serializerOptions }
            ),
            AIFunctionFactory.Create(
                ReplaceAsync,
                new AIFunctionFactoryOptions { Name = ReplaceToolName, SerializerOptions = serializerOptions }
            ),
            AIFunctionFactory.Create(
                ReplaceLinesAsync,
                new AIFunctionFactoryOptions { Name = ReplaceLinesToolName, SerializerOptions = serializerOptions }
            ),
        ];
    }

    private ProjectAgentFileStore ResolveStore(string? directoryId)
    {
        if (directoryId == null)
        {
            return _primaryStore;
        }

        if (!Guid.TryParse(directoryId, out var id) || id == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Invalid Project directory ID.");
        }

        return _additionalStores.GetValueOrDefault(id)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound, "Project directory was not found.");
    }
}
