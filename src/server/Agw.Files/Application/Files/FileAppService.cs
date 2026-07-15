using Agw.Files.Services;

using Microsoft.Extensions.Logging;

namespace Agw.Files.Application.Files;

public sealed class FileAppService
{
    private static readonly HashSet<string> IgnoreDirectories = new()
    {
        "node_modules",
        "obj",
        "bin",
    };

    private static readonly HashSet<string> IgnoreFiles = new()
    {
        "tmpclaude*"
    };

    private readonly IGitCommandService _gitCommandService;
    private readonly ILogger<FileAppService> _logger;

    public FileAppService(
        IGitCommandService gitCommandService,
        ILogger<FileAppService> logger)
    {
        _gitCommandService = gitCommandService;
        _logger = logger;
    }

    public async Task<FileOperationResult<FileListOutput>> ListAsync(
        string path,
        bool diff,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(path))
        {
            return FileOperationResult<FileListOutput>.Missing("Directory not found");
        }

        if (recursive && diff)
        {
            return await GetAllChangedFilesAsync(path, cancellationToken);
        }

        var entries = Directory.GetFileSystemEntries(path);
        var items = new List<FileListEntry>();
        GitChangedFiles? changedFiles = null;
        if (diff)
        {
            changedFiles = await _gitCommandService.GetChangedFilesAsync(path, cancellationToken);
            if (changedFiles == null || changedFiles.FileStatuses.Count == 0)
            {
                return FileOperationResult<FileListOutput>.Succeeded(new FileListOutput([]));
            }
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(entry);
            var directoryInfo = new DirectoryInfo(entry);

            if (diff && changedFiles != null)
            {
                if (fileInfo.Exists)
                {
                    if (!changedFiles.FileStatuses.ContainsKey(entry))
                    {
                        continue;
                    }
                }
                else if (directoryInfo.Exists)
                {
                    var hasChangedDescendant = changedFiles.FileStatuses.Keys.Any(
                        file => file.StartsWith(entry + Path.DirectorySeparatorChar));
                    if (!hasChangedDescendant)
                    {
                        continue;
                    }
                }
            }

            items.Add(new FileListEntry(
                Path.GetFileName(entry),
                entry,
                directoryInfo.Exists ? "directory" : "file",
                fileInfo.Exists ? fileInfo.Length : null,
                fileInfo.Exists ? fileInfo.LastWriteTimeUtc : directoryInfo.LastWriteTimeUtc,
                changedFiles?.FileStatuses.GetValueOrDefault(entry)));
        }

        if (diff && changedFiles != null)
        {
            foreach (var deletedFile in changedFiles.DeletedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var deletedDirectory = Path.GetDirectoryName(deletedFile);
                if (!string.Equals(deletedDirectory, path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                items.Add(new FileListEntry(
                    Path.GetFileName(deletedFile),
                    deletedFile,
                    "file",
                    null,
                    null,
                    "deleted"));
            }
        }

        var sortedItems = items
            .OrderBy(item => item.Type == "file")
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return FileOperationResult<FileListOutput>.Succeeded(new FileListOutput(sortedItems));
    }

    public async Task<FileOperationResult<string>> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return FileOperationResult<string>.Missing("File not found");
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return FileOperationResult<string>.Succeeded(content);
    }

    public async Task<FileOperationResult<FileDiffOutput>> DiffAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return FileOperationResult<FileDiffOutput>.Missing("File not found");
        }

        var result = await _gitCommandService.GetDiffAsync(path, cancellationToken);
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

    public Task<FileOperationResult<FileMutationOutput>> DeleteAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Deleted file: {Path}", path);
            return Task.FromResult(FileOperationResult<FileMutationOutput>.Succeeded(
                new FileMutationOutput(true, "File deleted successfully")));
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            _logger.LogInformation("Deleted directory: {Path}", path);
            return Task.FromResult(FileOperationResult<FileMutationOutput>.Succeeded(
                new FileMutationOutput(true, "Directory deleted successfully")));
        }

        return Task.FromResult(FileOperationResult<FileMutationOutput>.Missing(
            "File or directory not found"));
    }

    public async Task<FileOperationResult<FileMutationOutput>> ResetAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return FileOperationResult<FileMutationOutput>.Missing("File not found");
        }

        var result = await _gitCommandService.ResetFileAsync(path, cancellationToken);
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

        _logger.LogInformation("Reset file to HEAD: {Path}", path);
        return FileOperationResult<FileMutationOutput>.Succeeded(
            new FileMutationOutput(true, result.Message));
    }

    public Task<FileOperationResult<FileSearchOutput>> SearchAsync(
        string path,
        string? keyword,
        int limit,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(path))
        {
            return Task.FromResult(FileOperationResult<FileSearchOutput>.Missing(
                "Directory not found"));
        }

        keyword ??= string.Empty;
        var results = new List<FileSearchEntry>();
        if (recursive)
        {
            SearchFilesRecursive(path, path, keyword, limit, results, cancellationToken);
        }
        else
        {
            SearchFilesNonRecursive(path, keyword, limit, results, cancellationToken);
        }

        var sortedResults = results
            .OrderBy(result => result.Type == "file")
            .ThenBy(result => result.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return Task.FromResult(FileOperationResult<FileSearchOutput>.Succeeded(
            new FileSearchOutput(sortedResults)));
    }

    private async Task<FileOperationResult<FileListOutput>> GetAllChangedFilesAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var changedFiles = await _gitCommandService.GetChangedFilesAsync(
            directoryPath,
            cancellationToken);
        if (changedFiles == null || changedFiles.FileStatuses.Count == 0)
        {
            return FileOperationResult<FileListOutput>.Succeeded(new FileListOutput([]));
        }

        var items = new List<FileListEntry>();
        foreach (var (filePath, status) in changedFiles.FileStatuses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!filePath.StartsWith(directoryPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(filePath, directoryPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(filePath))
            {
                items.Add(new FileListEntry(
                    Path.GetFileName(filePath),
                    filePath,
                    "file",
                    null,
                    null,
                    status));
                continue;
            }

            var fileInfo = new FileInfo(filePath);
            items.Add(new FileListEntry(
                fileInfo.Name,
                filePath,
                "file",
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc,
                status));
        }

        var sortedItems = items
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return FileOperationResult<FileListOutput>.Succeeded(new FileListOutput(sortedItems));
    }

    private static string GetSearchRelativePath(string rootPath, string path, bool isDirectory)
    {
        var relativePath = Path.GetRelativePath(rootPath, path)
            .Replace(Path.DirectorySeparatorChar, '/');
        return isDirectory ? $"{relativePath.TrimEnd('/')}/" : relativePath;
    }

    private static bool MatchesSearchKeyword(string relativePath, string keyword)
    {
        return relativePath.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static void SearchFilesRecursive(
        string rootPath,
        string currentPath,
        string keyword,
        int limit,
        List<FileSearchEntry> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentDirectoryName = new DirectoryInfo(currentPath).Name;
        if (currentDirectoryName.StartsWith('.') || IgnoreDirectories.Contains(currentDirectoryName))
        {
            return;
        }

        if (results.Count >= limit)
        {
            return;
        }

        var directories = new List<string>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(currentPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directoryName = new DirectoryInfo(directory).Name;
                if (directoryName.StartsWith('.') || IgnoreDirectories.Contains(directoryName))
                {
                    continue;
                }

                directories.Add(directory);
                var relativePath = GetSearchRelativePath(rootPath, directory, isDirectory: true);
                if (!MatchesSearchKeyword(relativePath, keyword))
                {
                    continue;
                }

                results.Add(new FileSearchEntry(directory, relativePath, "directory"));
                if (results.Count >= limit)
                {
                    return;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(currentPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(file);
                if (ShouldIgnoreFile(fileInfo.Name))
                {
                    continue;
                }

                var relativePath = GetSearchRelativePath(rootPath, fileInfo.FullName, isDirectory: false);
                if (!MatchesSearchKeyword(relativePath, keyword))
                {
                    continue;
                }

                results.Add(new FileSearchEntry(fileInfo.FullName, relativePath, "file"));
                if (results.Count >= limit)
                {
                    return;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            foreach (var directory in directories)
            {
                SearchFilesRecursive(
                    rootPath,
                    directory,
                    keyword,
                    limit,
                    results,
                    cancellationToken);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void SearchFilesNonRecursive(
        string rootPath,
        string keyword,
        int limit,
        List<FileSearchEntry> results,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(rootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directoryName = new DirectoryInfo(directory).Name;
                if (directoryName.StartsWith('.') || IgnoreDirectories.Contains(directoryName))
                {
                    continue;
                }

                var relativePath = GetSearchRelativePath(rootPath, directory, isDirectory: true);
                if (!MatchesSearchKeyword(relativePath, keyword))
                {
                    continue;
                }

                results.Add(new FileSearchEntry(directory, relativePath, "directory"));
                if (results.Count >= limit)
                {
                    return;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(rootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(file);
                var relativePath = GetSearchRelativePath(rootPath, fileInfo.FullName, isDirectory: false);
                if (!MatchesSearchKeyword(relativePath, keyword))
                {
                    continue;
                }

                results.Add(new FileSearchEntry(fileInfo.FullName, relativePath, "file"));
                if (results.Count >= limit)
                {
                    return;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
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
}
