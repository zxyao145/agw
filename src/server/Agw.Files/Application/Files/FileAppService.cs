using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Files.Application.Storage.Local;
using Agw.Files.Services;

using Microsoft.Extensions.Logging;

namespace Agw.Files.Application.Files;

public sealed class FileAppService
{
    private const string GitRequiresLocalFileSystem =
        "Git operations are only supported for local project file systems";

    private static readonly HashSet<string> IgnoreDirectories = new()
    {
        "node_modules",
        "obj",
        "bin"
    };

    private static readonly HashSet<string> IgnoreFiles = new()
    {
        "tmpclaude*"
    };

    private readonly IAgwFileSystemResolver _fileSystemResolver;
    private readonly IGitCommandService _gitCommandService;
    private readonly ILogger<FileAppService> _logger;

    public FileAppService(
        IAgwFileSystemResolver fileSystemResolver,
        IGitCommandService gitCommandService,
        ILogger<FileAppService> logger)
    {
        _fileSystemResolver = fileSystemResolver;
        _gitCommandService = gitCommandService;
        _logger = logger;
    }

    public async Task<FileOperationResult<FileListOutput>> ListAsync(
        Guid projectId,
        string? path,
        bool diff,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken);
        if (fileSystem == null)
        {
            return FileOperationResult<FileListOutput>.Invalid("Project ID is required");
        }

        path ??= string.Empty;
        if (!await fileSystem.ExistsDirectoryAsync(path, cancellationToken))
        {
            return FileOperationResult<FileListOutput>.Missing("Directory not found");
        }

        if (diff && fileSystem is not LocalFileSystem)
        {
            return FileOperationResult<FileListOutput>.Invalid(GitRequiresLocalFileSystem);
        }

        if (recursive && diff)
        {
            return await GetAllChangedFilesAsync(
                (LocalFileSystem)fileSystem,
                path,
                cancellationToken);
        }

        GitChangedFiles? changedFiles = null;
        LocalFileSystem? local = null;
        if (diff)
        {
            local = (LocalFileSystem)fileSystem;
            var physicalPath = local.ResolvePhysicalPath(path);
            changedFiles = await _gitCommandService.GetChangedFilesAsync(
                physicalPath,
                cancellationToken);
            if (changedFiles == null || changedFiles.FileStatuses.Count == 0)
            {
                return FileOperationResult<FileListOutput>.Succeeded(new FileListOutput([]));
            }
        }

        var items = new List<FileListEntry>();
        await foreach (var entry in fileSystem.EnumerateAsync(
                           path,
                           "*",
                           recursive: false,
                           cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            GitFileStatus? gitStatus = null;
            if (changedFiles != null && local != null)
            {
                var physicalEntryPath = local.ResolvePhysicalPath(entry.Path);
                if (entry.IsDirectory)
                {
                    var descendantStatuses = changedFiles.FileStatuses
                        .Where(change => IsPathUnderDirectory(change.Key, physicalEntryPath))
                        .Select(change => change.Value)
                        .ToList();
                    if (descendantStatuses.Count == 0)
                    {
                        continue;
                    }

                    gitStatus = CombineGitStatuses(descendantStatuses);
                }
                else if (!changedFiles.FileStatuses.TryGetValue(physicalEntryPath, out gitStatus))
                {
                    continue;
                }
            }

            items.Add(ToListEntry(entry, gitStatus));
        }

        if (changedFiles != null && local != null)
        {
            var physicalDirectory = local.ResolvePhysicalPath(path);
            foreach (var deletedFile in changedFiles.DeletedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(
                        Path.GetDirectoryName(deletedFile),
                        physicalDirectory,
                        PathComparison))
                {
                    continue;
                }

                var relativePath = local.GetRelativePath(deletedFile);
                var normalizedPath = NormalizePath(relativePath);
                if (items.Any(item => string.Equals(item.Path, normalizedPath, PathComparison)))
                {
                    continue;
                }

                var gitStatus = changedFiles.FileStatuses[deletedFile];
                items.Add(new FileListEntry(
                    GetFileName(relativePath),
                    normalizedPath,
                    "file",
                    null,
                    null,
                    gitStatus.AggregateStatus,
                    gitStatus.StagedStatus,
                    gitStatus.UnstagedStatus));
            }
        }

        var sortedItems = items
            .OrderBy(item => item.Type == "file")
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return FileOperationResult<FileListOutput>.Succeeded(new FileListOutput(sortedItems));
    }

    public async Task<FileOperationResult<string>> ReadAsync(
        Guid projectId,
        string? path,
        CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken);
        if (fileSystem == null)
        {
            return FileOperationResult<string>.Invalid("Project ID is required");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<string>.Invalid("Path parameter is required");
        }

        if (!await fileSystem.ExistsFileAsync(path, cancellationToken))
        {
            return FileOperationResult<string>.Missing("File not found");
        }

        var content = await fileSystem.ReadAllTextAsync(path, cancellationToken);
        return FileOperationResult<string>.Succeeded(content);
    }

    public async Task<FileOperationResult<FileDiffOutput>> DiffAsync(
        Guid projectId,
        string? path,
        string? scope,
        CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken);
        if (fileSystem == null)
        {
            return FileOperationResult<FileDiffOutput>.Invalid("Project ID is required");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<FileDiffOutput>.Invalid("Path parameter is required");
        }

        if (fileSystem is not LocalFileSystem localFileSystem)
        {
            return FileOperationResult<FileDiffOutput>.Invalid(GitRequiresLocalFileSystem);
        }

        if (!TryParseDiffScope(scope, out var diffScope))
        {
            return FileOperationResult<FileDiffOutput>.Invalid(
                "Scope must be 'staged' or 'unstaged'");
        }

        var physicalPath = localFileSystem.ResolvePhysicalPath(path);
        if (!await fileSystem.ExistsFileAsync(path, cancellationToken))
        {
            var changedFiles = await _gitCommandService.GetChangedFilesAsync(
                physicalPath,
                cancellationToken);
            if (changedFiles == null
                || !changedFiles.FileStatuses.TryGetValue(physicalPath, out var gitStatus)
                || gitStatus.GetStatus(diffScope) == null)
            {
                return FileOperationResult<FileDiffOutput>.Missing("File not found");
            }
        }

        var result = await _gitCommandService.GetDiffAsync(
            physicalPath,
            cancellationToken,
            diffScope);
        if (!result.Success)
        {
            _logger.LogWarning("Git diff failed: {Error}", result.Error);
            return FileOperationResult<FileDiffOutput>.Invalid("Git diff failed", result.Error);
        }

        return FileOperationResult<FileDiffOutput>.Succeeded(new FileDiffOutput(
            result.Diff,
            result.Unchanged,
            result.OriginalContent));
    }

    public async Task<FileOperationResult<FileMutationOutput>> DeleteAsync(
        Guid projectId,
        string? path,
        CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken);
        if (fileSystem == null)
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Project ID is required");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Path parameter is required");
        }

        var isFile = await fileSystem.ExistsFileAsync(path, cancellationToken);
        var isDirectory = !isFile
            && await fileSystem.ExistsDirectoryAsync(path, cancellationToken);
        if (!isFile && !isDirectory)
        {
            return FileOperationResult<FileMutationOutput>.Missing(
                "File or directory not found");
        }

        await fileSystem.DeleteAsync(path, cancellationToken);
        _logger.LogInformation(
            "Deleted {EntryType} in project {ProjectId}: {Path}",
            isDirectory ? "directory" : "file",
            projectId,
            path);

        return FileOperationResult<FileMutationOutput>.Succeeded(new FileMutationOutput(
            true,
            isDirectory ? "Directory deleted successfully" : "File deleted successfully"));
    }

    public async Task<FileOperationResult<FileMutationOutput>> ResetAsync(
        Guid projectId,
        string? path,
        CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken);
        if (fileSystem == null)
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Project ID is required");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Path parameter is required");
        }

        if (fileSystem is not LocalFileSystem localFileSystem)
        {
            return FileOperationResult<FileMutationOutput>.Invalid(GitRequiresLocalFileSystem);
        }

        if (!await fileSystem.ExistsFileAsync(path, cancellationToken))
        {
            return FileOperationResult<FileMutationOutput>.Missing("File not found");
        }

        var physicalPath = localFileSystem.ResolvePhysicalPath(path);
        var result = await _gitCommandService.ResetFileAsync(physicalPath, cancellationToken);
        if (!result.Success && result.IsClientError)
        {
            return FileOperationResult<FileMutationOutput>.Invalid(result.Message);
        }

        if (!result.Success && !string.IsNullOrEmpty(result.Error))
        {
            _logger.LogError("Git reset failed: {Error}", result.Error);
            return FileOperationResult<FileMutationOutput>.Failed("Git reset failed", result.Error);
        }

        if (!result.Success)
        {
            return FileOperationResult<FileMutationOutput>.Succeeded(
                new FileMutationOutput(false, result.Message));
        }

        _logger.LogInformation(
            "Reset file to HEAD in project {ProjectId}: {Path}",
            projectId,
            path);
        return FileOperationResult<FileMutationOutput>.Succeeded(
            new FileMutationOutput(true, result.Message));
    }

    public Task<FileOperationResult<FileMutationOutput>> StageAsync(
        Guid projectId,
        string? path,
        CancellationToken cancellationToken = default)
    {
        return SetStagedAsync(projectId, path, staged: true, cancellationToken);
    }

    public Task<FileOperationResult<FileMutationOutput>> UnstageAsync(
        Guid projectId,
        string? path,
        CancellationToken cancellationToken = default)
    {
        return SetStagedAsync(projectId, path, staged: false, cancellationToken);
    }

    public async Task<FileOperationResult<FileSearchOutput>> SearchAsync(
        Guid projectId,
        string? path,
        string? keyword,
        int limit,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken);
        if (fileSystem == null)
        {
            return FileOperationResult<FileSearchOutput>.Invalid("Project ID is required");
        }

        path ??= string.Empty;
        if (!await fileSystem.ExistsDirectoryAsync(path, cancellationToken))
        {
            return FileOperationResult<FileSearchOutput>.Missing("Directory not found");
        }

        keyword ??= string.Empty;
        if (limit <= 0 || IsIgnoredDirectoryPath(path))
        {
            return FileOperationResult<FileSearchOutput>.Succeeded(new FileSearchOutput([]));
        }

        var results = new List<FileSearchEntry>();
        await SearchDirectoryAsync(
            fileSystem,
            path,
            path,
            keyword,
            limit,
            recursive,
            results,
            cancellationToken);

        var sortedResults = results
            .OrderBy(result => result.Type == "file")
            .ThenBy(result => result.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return FileOperationResult<FileSearchOutput>.Succeeded(
            new FileSearchOutput(sortedResults));
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private async Task<IAgwFileSystem?> ResolveFileSystemAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        return projectId == Guid.Empty
            ? null
            : await _fileSystemResolver.ResolveAsync(projectId, cancellationToken);
    }

    private async Task<FileOperationResult<FileMutationOutput>> SetStagedAsync(
        Guid projectId,
        string? path,
        bool staged,
        CancellationToken cancellationToken)
    {
        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken);
        if (fileSystem == null)
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Project ID is required");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Path parameter is required");
        }

        if (fileSystem is not LocalFileSystem localFileSystem)
        {
            return FileOperationResult<FileMutationOutput>.Invalid(GitRequiresLocalFileSystem);
        }

        if (TargetsWorkspaceRoot(path))
        {
            return FileOperationResult<FileMutationOutput>.Invalid(
                "Workspace root cannot be staged or unstaged");
        }

        var physicalPath = localFileSystem.ResolvePhysicalPath(path);
        var result = await _gitCommandService.SetStagedAsync(
            physicalPath,
            staged,
            cancellationToken);
        if (!result.Success && result.IsClientError)
        {
            return FileOperationResult<FileMutationOutput>.Invalid(result.Message, result.Error);
        }

        if (!result.Success)
        {
            _logger.LogError(
                "Git {Operation} failed: {Error}",
                staged ? "stage" : "unstage",
                result.Error);
            return FileOperationResult<FileMutationOutput>.Failed(result.Message, result.Error);
        }

        _logger.LogInformation(
            "{Operation} changes in project {ProjectId}: {Path}",
            staged ? "Staged" : "Unstaged",
            projectId,
            path);
        return FileOperationResult<FileMutationOutput>.Succeeded(
            new FileMutationOutput(true, result.Message));
    }

    private async Task<FileOperationResult<FileListOutput>> GetAllChangedFilesAsync(
        LocalFileSystem fileSystem,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var physicalDirectory = fileSystem.ResolvePhysicalPath(directoryPath);
        var changedFiles = await _gitCommandService.GetChangedFilesAsync(
            physicalDirectory,
            cancellationToken);
        if (changedFiles == null || changedFiles.FileStatuses.Count == 0)
        {
            return FileOperationResult<FileListOutput>.Succeeded(new FileListOutput([]));
        }

        var items = new List<FileListEntry>();
        foreach (var (physicalPath, status) in changedFiles.FileStatuses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPathUnderDirectory(physicalPath, physicalDirectory)
                || string.Equals(physicalPath, physicalDirectory, PathComparison))
            {
                continue;
            }

            var relativePath = fileSystem.GetRelativePath(physicalPath);
            var entry = await fileSystem.StatAsync(relativePath, cancellationToken);
            items.Add(entry == null
                ? new FileListEntry(
                    GetFileName(relativePath),
                    NormalizePath(relativePath),
                    "file",
                    null,
                    null,
                    status.AggregateStatus,
                    status.StagedStatus,
                    status.UnstagedStatus)
                : ToListEntry(entry, status));
        }

        var sortedItems = items
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return FileOperationResult<FileListOutput>.Succeeded(new FileListOutput(sortedItems));
    }

    private static FileListEntry ToListEntry(FileEntry entry, GitFileStatus? gitStatus)
    {
        var path = NormalizePath(entry.Path);
        return new FileListEntry(
            GetFileName(path),
            path,
            entry.IsDirectory ? "directory" : "file",
            entry.IsDirectory ? null : entry.Size,
            entry.LastModifiedUtc,
            gitStatus?.AggregateStatus,
            gitStatus?.StagedStatus,
            gitStatus?.UnstagedStatus);
    }

    private static GitFileStatus CombineGitStatuses(IEnumerable<GitFileStatus> statuses)
    {
        var values = statuses.ToList();
        return new GitFileStatus(
            GetAggregatedStatus(values.Select(status => status.StagedStatus)),
            GetAggregatedStatus(values.Select(status => status.UnstagedStatus)));
    }

    private static string? GetAggregatedStatus(IEnumerable<string?> statuses)
    {
        var values = statuses.Where(status => status != null).ToHashSet();
        if (values.Contains("modified")) return "modified";
        if (values.Contains("added")) return "added";
        if (values.Contains("untracked")) return "untracked";
        if (values.Contains("deleted")) return "deleted";
        return null;
    }

    private static bool TryParseDiffScope(string? value, out GitDiffScope scope)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            scope = GitDiffScope.All;
            return true;
        }

        if (value.Equals("staged", StringComparison.OrdinalIgnoreCase))
        {
            scope = GitDiffScope.Staged;
            return true;
        }

        if (value.Equals("unstaged", StringComparison.OrdinalIgnoreCase))
        {
            scope = GitDiffScope.Unstaged;
            return true;
        }

        scope = GitDiffScope.All;
        return false;
    }

    private static bool IsPathUnderDirectory(string candidatePath, string directoryPath)
    {
        if (string.Equals(candidatePath, directoryPath, PathComparison))
        {
            return true;
        }

        var relativePath = Path.GetRelativePath(directoryPath, candidatePath);
        return !Path.IsPathRooted(relativePath)
            && !string.Equals(relativePath, "..", PathComparison)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison);
    }

    private static string GetPathRelativeTo(string entryPath, string rootPath)
    {
        var normalizedEntry = NormalizePath(entryPath).Trim('/');
        var normalizedRoot = NormalizePath(rootPath).Trim('/');
        if (string.IsNullOrEmpty(normalizedRoot))
        {
            return normalizedEntry;
        }

        if (string.Equals(normalizedEntry, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var prefix = $"{normalizedRoot}/";
        return normalizedEntry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedEntry[prefix.Length..]
            : normalizedEntry;
    }

    private static async Task SearchDirectoryAsync(
        IAgwFileSystem fileSystem,
        string rootPath,
        string currentPath,
        string keyword,
        int limit,
        bool recursive,
        List<FileSearchEntry> results,
        CancellationToken cancellationToken)
    {
        if (results.Count >= limit)
        {
            return;
        }

        var entries = new List<FileEntry>();
        await foreach (var entry in fileSystem.EnumerateAsync(
                           currentPath,
                           "*",
                           recursive: false,
                           cancellationToken))
        {
            entries.Add(entry);
        }

        var directories = new List<FileEntry>();
        foreach (var entry in entries.Where(entry => entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = GetPathRelativeTo(entry.Path, rootPath);
            if (ShouldIgnoreSearchEntry(
                    relativePath,
                    isDirectory: true,
                    recursive: recursive))
            {
                continue;
            }

            directories.Add(entry);
            AddSearchResult(entry, relativePath, keyword, limit, results);
            if (results.Count >= limit)
            {
                return;
            }
        }

        foreach (var entry in entries.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = GetPathRelativeTo(entry.Path, rootPath);
            if (ShouldIgnoreSearchEntry(
                    relativePath,
                    isDirectory: false,
                    recursive: recursive))
            {
                continue;
            }

            AddSearchResult(entry, relativePath, keyword, limit, results);
            if (results.Count >= limit)
            {
                return;
            }
        }

        if (!recursive)
        {
            return;
        }

        foreach (var directory in directories)
        {
            await SearchDirectoryAsync(
                fileSystem,
                rootPath,
                directory.Path,
                keyword,
                limit,
                recursive: true,
                results,
                cancellationToken);
            if (results.Count >= limit)
            {
                return;
            }
        }
    }

    private static void AddSearchResult(
        FileEntry entry,
        string relativePath,
        string keyword,
        int limit,
        List<FileSearchEntry> results)
    {
        if (results.Count >= limit)
        {
            return;
        }

        var resultPath = entry.IsDirectory
            ? $"{relativePath.TrimEnd('/')}/"
            : relativePath;
        if (!resultPath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        results.Add(new FileSearchEntry(
            NormalizePath(entry.Path),
            resultPath,
            entry.IsDirectory ? "directory" : "file"));
    }

    private static bool IsIgnoredDirectoryPath(string path)
    {
        return NormalizePath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.StartsWith('.') || IgnoreDirectories.Contains(segment));
    }

    private static bool ShouldIgnoreSearchEntry(
        string relativePath,
        bool isDirectory,
        bool recursive)
    {
        var segments = NormalizePath(relativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var directorySegmentCount = isDirectory
            ? segments.Length
            : Math.Max(0, segments.Length - 1);
        if (segments
            .Take(directorySegmentCount)
            .Any(segment => segment.StartsWith('.') || IgnoreDirectories.Contains(segment)))
        {
            return true;
        }

        return recursive
            && !isDirectory
            && segments.Length > 0
            && ShouldIgnoreFile(segments[^1]);
    }

    private static bool ShouldIgnoreFile(string fileName)
    {
        foreach (var pattern in IgnoreFiles)
        {
            if (pattern.StartsWith('*') && fileName.EndsWith(pattern[1..]))
            {
                return true;
            }

            if (pattern.EndsWith('*') && fileName.StartsWith(pattern[..^1]))
            {
                return true;
            }

            if (pattern == fileName)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetFileName(string path)
    {
        var normalized = NormalizePath(path).TrimEnd('/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : normalized;
    }

    private static bool TargetsWorkspaceRoot(string path)
    {
        var depth = 0;
        foreach (var segment in NormalizePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (depth == 0)
                {
                    return false;
                }

                depth -= 1;
                continue;
            }

            depth += 1;
        }

        return depth == 0;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}
