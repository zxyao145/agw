using Agw.Files.Abstracts.Dtos;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;

namespace Agw.Tools.Impl.ToolBlocks.Storage;

/// <summary>
/// 基于 Project 目录 <see cref="IAgwFileSystem"/> 的文件工具存储；文件操作与路径校验都由 <see cref="IAgwFileSystem"/> 完成，
/// 这里只负责选择目录以及限制返回给模型的内容规模。
/// File-tool storage over a Project directory's <see cref="IAgwFileSystem"/>. File operations and path validation are
/// done by <see cref="IAgwFileSystem"/>; this class only selects the directory and bounds what reaches the model.
/// </summary>
public sealed class ProjectAgentFileStore : AgwAgentFileStore
{
    private const int MaxListEntries = 1_000;
    private const long MaxReadableFileSizeBytes = 128 * 1024;
    private const int MaxSearchHits = 200;
    private const int MaxSearchFiles = 10_000;
    private const long MaxSearchFileSizeBytes = 5 * 1024 * 1024;
    private const long MaxSearchTotalBytes = 128 * 1024 * 1024;
    private const int MaxSearchLineCharacters = 4 * 1024;
    private const int MaxSearchResultCharacters = 64 * 1024;
    private const string TruncatedLineSuffix = "... [truncated]";

    private static readonly string[] ExcludedSearchDirectoryNames =
    [
        ".git",
        ".worktrees",
        "node_modules",
        "bin",
        "obj",
        ".next",
        ".turbo",
        "dist",
    ];

    private readonly IAgwFileSystemResolver _resolver;
    private readonly Guid _projectId;
    private readonly string? _rootPath;
    private readonly ProjectWorkspaceSnapshot? _workspaceSnapshot;
    private readonly Guid? _directoryId;

    public ProjectAgentFileStore(IAgwFileSystemResolver resolver, Guid projectId)
        : this(resolver, projectId, null) { }

    public ProjectAgentFileStore(
        IAgwFileSystemResolver resolver,
        Guid projectId,
        string? rootPath,
        ProjectWorkspaceSnapshot? workspaceSnapshot = null,
        Guid? directoryId = null
    )
    {
        _resolver = resolver;
        _projectId = projectId;
        _workspaceSnapshot = workspaceSnapshot ?? ExecutionContextSlot.GetWorkspaceSnapshot(projectId);
        _directoryId = directoryId;
        _rootPath = string.IsNullOrWhiteSpace(rootPath) ? null : rootPath;
    }

    public override async Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        await fileSystem.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 创建文件；文件已存在时不写入并返回 false。
    /// Creates a file; returns false without writing when the file already exists.
    /// </summary>
    public async Task<bool> CreateFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return await fileSystem.CreateTextFileAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return await IsReadableFileAsync(fileSystem, path, cancellationToken).ConfigureAwait(false)
            ? await fileSystem.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// 读取从 1 开始、首尾都包含的行区间，每行保留结束符；文件不存在时返回 <see langword="null"/>。
    /// Reads the 1-based inclusive line range with terminators kept, or returns <see langword="null"/> when the file does
    /// not exist.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ReadLinesAsync(
        string path,
        int startLine,
        int? endLine,
        CancellationToken cancellationToken = default
    )
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return await IsReadableFileAsync(fileSystem, path, cancellationToken).ConfigureAwait(false)
            ? await fileSystem.ReadLinesAsync(path, startLine, endLine, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public override async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (!await fileSystem.ExistsFileAsync(path, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await fileSystem.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override async Task<IReadOnlyList<AgwFileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default
    )
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<AgwFileStoreEntry>();
        await foreach (
            var entry in fileSystem
                .EnumerateAsync(directory, "*", recursive: false, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            if (entries.Count >= MaxListEntries)
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Directory '{directory}' contains more than {MaxListEntries} entries. "
                        + "List a narrower directory instead."
                );
            }

            entries.Add(
                new AgwFileStoreEntry(
                    Path.GetFileName(entry.Path.TrimEnd('/', '\\')),
                    entry.IsDirectory ? AgwFileStoreEntry.Directory : AgwFileStoreEntry.File
                )
            );
        }

        return entries
            .OrderByDescending(static entry => entry.Type == AgwFileStoreEntry.Directory)
            .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public override async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return await fileSystem.ExistsFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<AgwFileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern = null,
        bool recursive = false,
        CancellationToken cancellationToken = default
    )
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<AgwFileSearchResult>();
        var resultCharacters = 0;
        var budgetExhausted = false;
        await foreach (
            var fileResult in fileSystem
                .SearchAsync(
                    directory,
                    new SearchOptions(
                        regexPattern,
                        IsRegex: true,
                        CaseInsensitive: true,
                        FilenameGlob: globPattern,
                        MaxHits: MaxSearchHits,
                        Recursive: recursive,
                        ExcludedDirectoryNames: ExcludedSearchDirectoryNames,
                        MaxFiles: MaxSearchFiles,
                        MaxFileSizeBytes: MaxSearchFileSizeBytes,
                        MaxTotalBytes: MaxSearchTotalBytes
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false)
        )
        {
            // 按字符预算截断返回给模型的命中行；预算用完时停止整个搜索。
            // Truncate the lines returned to the model within a character budget; stop the whole search once it runs out.
            AgwFileSearchResult? result = null;
            foreach (var match in fileResult.MatchingLines)
            {
                var fixedCharacters = result == null ? fileResult.FileName.Length : 0;
                var lineCopies = result == null ? 2 : 1;
                var availableLineCharacters =
                    (MaxSearchResultCharacters - resultCharacters - fixedCharacters) / lineCopies;
                var maxLineCharacters = Math.Min(MaxSearchLineCharacters, availableLineCharacters);
                if (
                    maxLineCharacters <= 0
                    || (match.Line.Length > maxLineCharacters && maxLineCharacters <= TruncatedLineSuffix.Length)
                )
                {
                    budgetExhausted = true;
                    break;
                }

                var line = TruncateSearchLine(match.Line, maxLineCharacters);
                if (result == null)
                {
                    result = new AgwFileSearchResult
                    {
                        FileName = fileResult.FileName,
                        Snippet = line,
                        MatchingLines = [],
                    };
                    results.Add(result);
                    resultCharacters += fixedCharacters + line.Length;
                }

                result.MatchingLines.Add(new AgwFileSearchMatch { LineNumber = match.LineNumber, Line = line });
                resultCharacters += line.Length;
            }

            if (budgetExhausted)
            {
                break;
            }
        }

        return results.OrderBy(static result => result.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public override async Task<int?> ReplaceTextAsync(
        string path,
        string oldString,
        string newString,
        bool replaceAll,
        CancellationToken cancellationToken = default
    )
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return await IsReadableFileAsync(fileSystem, path, cancellationToken).ConfigureAwait(false)
            ? await fileSystem
                .ReplaceTextAsync(path, oldString, newString, replaceAll, cancellationToken)
                .ConfigureAwait(false)
            : null;
    }

    public override async Task<bool> ReplaceLinesAsync(
        string path,
        IReadOnlyList<AgwFileLineEdit> edits,
        CancellationToken cancellationToken = default
    )
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsReadableFileAsync(fileSystem, path, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await fileSystem.ReplaceLinesAsync(path, edits, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        await fileSystem.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IAgwFileSystem> ResolveAsync(CancellationToken cancellationToken)
    {
        var fileSystem =
            (
                _workspaceSnapshot == null
                    ? await _resolver.ResolveAsync(_projectId, _directoryId, cancellationToken).ConfigureAwait(false)
                    : await _resolver
                        .ResolveSnapshotAsync(_projectId, _workspaceSnapshot, _directoryId, cancellationToken)
                        .ConfigureAwait(false)
            ) ?? throw new AgwException(ErrorCodes.ResourceNotFound, "Project was not found.");
        return _rootPath == null ? fileSystem : fileSystem.GetSubFileSystem(_rootPath);
    }

    /// <summary>
    /// 判断路径是否为可读取的文件；文件超过读取上限时抛出异常，引导模型改用 grep。
    /// Returns whether the path is a readable file; a file over the read limit throws, pointing the model to grep.
    /// </summary>
    private static async Task<bool> IsReadableFileAsync(
        IAgwFileSystem fileSystem,
        string path,
        CancellationToken cancellationToken
    )
    {
        var entry = await fileSystem.StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (entry is not { IsDirectory: false })
        {
            return false;
        }

        if (entry.Size > MaxReadableFileSizeBytes)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"File '{path}' exceeds the 128 KiB file-access read limit. "
                    + "Use file_access_grep to locate the relevant content instead."
            );
        }

        return true;
    }

    private static string TruncateSearchLine(string line, int maxCharacters)
    {
        if (line.Length <= maxCharacters)
        {
            return line;
        }

        return string.Concat(line.AsSpan(0, maxCharacters - TruncatedLineSuffix.Length), TruncatedLineSuffix);
    }
}
