using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;

namespace Agw.Tools.ToolBlocks.Storage;

public sealed class ProjectAgentFileStore : AgentFileStore
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

    public ProjectAgentFileStore(IAgwFileSystemResolver resolver, Guid projectId)
        : this(resolver, projectId, null) { }

    public ProjectAgentFileStore(IAgwFileSystemResolver resolver, Guid projectId, string? rootPath)
    {
        _resolver = resolver;
        _projectId = projectId;
        _rootPath = string.IsNullOrWhiteSpace(rootPath) ? null : NormalizeScopedPath(rootPath, allowEmpty: false);
    }

    public override async Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        await fileSystem.WriteAllTextAsync(ScopePath(path), content, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        var scopedPath = ScopePath(path);
        var entry = await fileSystem.StatAsync(scopedPath, cancellationToken).ConfigureAwait(false);
        if (entry is not { IsDirectory: false })
        {
            return null;
        }

        if (entry.Size > MaxReadableFileSizeBytes)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"File '{path}' exceeds the 128 KiB file-access read limit. "
                    + "Use file_access_grep to locate the relevant content instead."
            );
        }

        return await fileSystem.ReadAllTextAsync(scopedPath, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        var scopedPath = ScopePath(path);
        if (!await fileSystem.ExistsFileAsync(scopedPath, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await fileSystem.DeleteAsync(scopedPath, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override async Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default
    )
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        var scopedDirectory = ScopePath(directory);
        var entries = new List<FileStoreEntry>();
        await foreach (
            var entry in fileSystem
                .EnumerateAsync(scopedDirectory, "*", recursive: false, cancellationToken)
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
                new FileStoreEntry(
                    Path.GetFileName(entry.Path.TrimEnd('/', '\\')),
                    entry.IsDirectory ? FileStoreEntry.Directory : FileStoreEntry.File
                )
            );
        }

        return entries
            .OrderByDescending(static entry => entry.Type == FileStoreEntry.Directory)
            .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public override async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return await fileSystem.ExistsFileAsync(ScopePath(path), cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern = null,
        bool recursive = false,
        CancellationToken cancellationToken = default
    )
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        var scopedDirectory = ScopePath(directory);
        var results = new Dictionary<string, FileSearchResult>(StringComparer.OrdinalIgnoreCase);
        var resultCharacters = 0;
        await foreach (
            var hit in fileSystem
                .SearchAsync(
                    scopedDirectory,
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
            var relativePath = GetPathRelativeToDirectory(scopedDirectory, hit.Path);
            if (!recursive && relativePath.Contains('/'))
            {
                continue;
            }

            var isFirstMatch = !results.TryGetValue(relativePath, out var result);
            var fixedCharacters = isFirstMatch ? relativePath.Length : 0;
            var lineCopies = isFirstMatch ? 2 : 1;
            var availableLineCharacters = (MaxSearchResultCharacters - resultCharacters - fixedCharacters) / lineCopies;
            var maxLineCharacters = Math.Min(MaxSearchLineCharacters, availableLineCharacters);
            if (
                maxLineCharacters <= 0
                || (hit.Line.Length > maxLineCharacters && maxLineCharacters <= TruncatedLineSuffix.Length)
            )
            {
                break;
            }

            var line = TruncateSearchLine(hit.Line, maxLineCharacters);
            if (result == null)
            {
                result = new FileSearchResult
                {
                    FileName = relativePath,
                    Snippet = line,
                    MatchingLines = [],
                };
                results.Add(relativePath, result);
                resultCharacters += fixedCharacters + line.Length;
            }

            result.MatchingLines.Add(new FileSearchMatch { LineNumber = hit.LineNumber, Line = line });
            resultCharacters += line.Length;
        }

        return results.Values.OrderBy(static result => result.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public override async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        await fileSystem.CreateDirectoryAsync(ScopePath(path), cancellationToken).ConfigureAwait(false);
    }

    private Task<IAgwFileSystem> ResolveAsync(CancellationToken cancellationToken) =>
        _resolver.ResolveAsync(_projectId, cancellationToken);

    private static string TruncateSearchLine(string line, int maxCharacters)
    {
        if (line.Length <= maxCharacters)
        {
            return line;
        }

        return string.Concat(line.AsSpan(0, maxCharacters - TruncatedLineSuffix.Length), TruncatedLineSuffix);
    }

    private string ScopePath(string path)
    {
        if (_rootPath == null)
        {
            return path;
        }

        var normalizedPath = NormalizeScopedPath(path, allowEmpty: true);
        return normalizedPath.Length == 0 ? _rootPath : $"{_rootPath}/{normalizedPath}";
    }

    private static string NormalizeScopedPath(string path, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalizedPath = path.Replace('\\', '/').Trim('/');
        if (
            (!allowEmpty && normalizedPath.Length == 0)
            || Path.IsPathRooted(path)
            || normalizedPath.Split('/').Any(static part => part is "." or "..")
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Scoped file-store paths must be non-rooted relative paths."
            );
        }

        return normalizedPath;
    }

    private static string GetPathRelativeToDirectory(string directory, string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        var normalizedDirectory = directory.Trim('/', '\\').Replace('\\', '/');
        if (normalizedDirectory.Length == 0)
        {
            return normalizedPath;
        }

        var prefix = normalizedDirectory + "/";
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedPath[prefix.Length..]
            : normalizedPath;
    }
}
