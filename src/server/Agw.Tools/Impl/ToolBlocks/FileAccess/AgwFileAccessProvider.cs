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
/// Tool permissions and approval come from the <see cref="FileAccessToolBlock"/> member metadata, and
/// <see cref="AgwFileReadonlyAccessProvider"/> exposes the read tools of <see cref="Tools"/> without creating others.
/// 每个工具都接受可选的 <c>directoryId</c>：省略或为 <see langword="null"/> 时使用主目录，否则必须是快照中某个附加目录的 ID。
/// 文件操作、路径校验和写入互斥都在 <see cref="IAgwFileSystem"/> 中完成，本类只定义工具并整理结果。
/// 工具权限与审批由 <see cref="FileAccessToolBlock"/> 的成员元数据决定；<see cref="AgwFileReadonlyAccessProvider"/>
/// 直接暴露 <see cref="Tools"/> 中的读取工具，不另外创建工具。
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

    private const string Instructions = $$"""
        ## File Access
        The `file_access_*` tools read, write, and manage files in the Project directories.
        Paths are relative to the selected Project directory.
        - Never delete or overwrite existing files unless the user explicitly asked for it.
        - {{
            "Locate directories with `file_access_ls` and search with `file_access_grep`, "
            + "limited by `directory` or `globPattern`."
        }}
        - Read a range with `file_access_read_lines`.
        When a read or search returns a `continuation`, pass it back with the same arguments to get the next page.
        - For a small edit, use `file_access_replace` or `file_access_replace_lines`.
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

    /// <summary>
    /// Gets the read and write tools, created once per provider.
    /// 本 Provider 的读取与写入工具，每个 Provider 只创建一次。
    /// </summary>
    internal IReadOnlyList<AITool> Tools => _tools ??= CreateTools();

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult(new AIContext { Instructions = Instructions, Tools = Tools });

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
        "Read file text with original line endings. A single page returns a string. Larger reads return content and a continuation, up to 200 lines or 16,384 UTF-16 code units per page."
    )]
    private async Task<object> ReadAsync(
        string fileName,
        [Description("Pass the previous continuation unchanged with the same file and directory.")]
            FileReadContinuation? continuation = null,
        [Description(DirectoryIdDescription)] string? directoryId = null,
        CancellationToken cancellationToken = default
    )
    {
        var page = await ResolveStore(directoryId)
            .ReadPageAsync(
                fileName,
                new ProjectAgentFileReadOptions(1, null, NumberedLines: false),
                continuation,
                cancellationToken
            )
            .ConfigureAwait(false);
        return ReadResult(page, fileName, continuation is not null);
    }

    [Description(
        "Read a 1-based inclusive line range. Omit endLine to read through EOF. Each line starts with its number and a tab and keeps its CR, LF, or CRLF ending. Pages hold up to 200 lines or 16,384 UTF-16 code units. Remove repeated line numbers before joining long-line fragments for editing."
    )]
    private async Task<object> ReadLinesAsync(
        string fileName,
        int startLine,
        int? endLine = null,
        [Description("Pass the previous continuation unchanged with the same file, directory and line range.")]
            FileReadContinuation? continuation = null,
        [Description(DirectoryIdDescription)] string? directoryId = null,
        CancellationToken cancellationToken = default
    )
    {
        var page = await ResolveStore(directoryId)
            .ReadPageAsync(
                fileName,
                new ProjectAgentFileReadOptions(startLine, endLine, NumberedLines: true),
                continuation,
                cancellationToken
            )
            .ConfigureAwait(false);
        return ReadResult(page, fileName, continuation is not null);
    }

    private static object ReadResult(ProjectAgentFileReadPage? page, string fileName, bool continuationRequested)
    {
        if (page is null)
        {
            return $"File '{fileName}' not found.";
        }

        return page.Continuation is not null || continuationRequested ? page : page.Content;
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
            Search recursively with a case-insensitive regular expression. The glob is relative to the search directory.
            Use '*' within a path segment and '**' across directories, as in "**/*.md".
            Returns up to 20 matching lines grouped by file. Pass the returned continuation with the same search to get the next page.
            Line numbers are 1-based and count lines terminated by \n, \r\n, or a lone \r, and content ending in a terminator has no extra empty line after it.
            """
    )]
    private async Task<GrepResult> GrepAsync(
        string regexPattern,
        string? globPattern = null,
        string? directory = null,
        [Description(DirectoryIdDescription)] string? directoryId = null,
        [Description("Continuation object from a previous page. Reuse it with the same search parameters.")]
            FileSearchPosition? continuation = null,
        CancellationToken cancellationToken = default
    )
    {
        if (continuation is { } cursor && (string.IsNullOrEmpty(cursor.FileName) || cursor.LineNumber < 1))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Invalid file search continuation.");
        }

        var searchPage = await ResolveStore(directoryId)
            .SearchForAccessAsync(
                directory ?? string.Empty,
                regexPattern,
                string.IsNullOrWhiteSpace(globPattern) ? null : globPattern,
                continuation,
                cancellationToken
            )
            .ConfigureAwait(false);

        var files = new List<GrepFileResult>();
        var remaining = ProjectAgentFileStore.SearchPageSize;
        FileSearchPosition? lastMatch = null;
        foreach (var result in searchPage.Results)
        {
            var matches = result.MatchingLines.Take(remaining).ToArray();
            if (matches.Length == 0)
            {
                continue;
            }

            files.Add(new GrepFileResult(result.FileName, matches));
            remaining -= matches.Length;
            var lastLine = matches[^1];
            lastMatch = new FileSearchPosition(result.FileName, lastLine.LineNumber);
            if (remaining == 0)
            {
                break;
            }
        }

        var matchingLineCount = searchPage.Results.Sum(static result => result.MatchingLines.Count);
        var hasMore = searchPage.HasMore || matchingLineCount > ProjectAgentFileStore.SearchPageSize;
        return new GrepResult(files, hasMore ? lastMatch : null);
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

    private sealed record GrepFileResult(string FileName, IReadOnlyList<AgwFileSearchMatch> MatchingLines);

    private sealed record GrepResult(IReadOnlyList<GrepFileResult> Files, FileSearchPosition? Continuation);
}
