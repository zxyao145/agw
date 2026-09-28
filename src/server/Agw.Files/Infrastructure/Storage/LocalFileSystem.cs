using System.IO.Enumeration;
using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Files.Infrastructure.Storage.Confined;
using Agw.Shared.Exceptions;

namespace Agw.Files.Infrastructure.Storage;

public sealed class LocalFileSystem : ILocalFileSystem
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly string _rootPath;
    private readonly string _rootFullPath;
    private readonly string _normalizedRoot;

    public LocalFileSystem(string rootPath)
    {
        _rootPath = rootPath;
        _rootFullPath = Path.GetFullPath(rootPath);
        _normalizedRoot =
            _rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    public string NormalizedRoot => _normalizedRoot;

    private string ResolvePath(string path)
    {
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);

        if (string.IsNullOrEmpty(normalized))
        {
            return Path.GetFullPath(_rootPath);
        }

        if (Path.IsPathRooted(normalized))
        {
            throw new AgwException(
                ErrorCodes.FilePathOutsideRoot,
                $"Path '{path}' must be relative to the file system root."
            );
        }

        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, normalized));

        if (!fullPath.StartsWith(_normalizedRoot, PathComparison))
        {
            throw new AgwException(
                ErrorCodes.FilePathOutsideRoot,
                $"Path '{path}' is outside the allowed root directory."
            );
        }

        return fullPath;
    }

    private string ResolveFilePath(string path)
    {
        var fullPath = ResolvePath(path);
        // ResolvePath 保证结果位于根目录下，长度不超过根目录前缀的只能是根目录本身。
        // ResolvePath keeps the result under the root, so only the root itself is no longer than the root prefix.
        if (fullPath.Length <= _normalizedRoot.Length)
        {
            throw new AgwException(ErrorCodes.FilePathRequired, "A file path must not be empty.");
        }

        return fullPath;
    }

    public string ResolvePhysicalPath(string path)
    {
        return ResolvePath(path);
    }

    public string GetRelativePath(string fullPath)
    {
        return ToRelativePath(fullPath);
    }

    public IAgwFileSystem Confine(IReadOnlyList<string> allowedRoots) =>
        ConfinedFileSystem.Create(_rootFullPath, allowedRoots);

    private string ToRelativePath(string fullPath)
    {
        if (fullPath.Equals(_rootFullPath, StringComparison.Ordinal))
        {
            return "";
        }
        return fullPath[_normalizedRoot.Length..].Replace(Path.DirectorySeparatorChar, '/');
    }

    public Task<bool> ExistsFileAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolvePath(path);
        return Task.FromResult(File.Exists(fullPath));
    }

    public Task<bool> ExistsDirectoryAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolvePath(path);
        return Task.FromResult(Directory.Exists(fullPath));
    }

    public Task<FileEntry?> StatAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolvePath(path);

        if (File.Exists(fullPath))
        {
            var info = new FileInfo(fullPath);
            return Task.FromResult<FileEntry?>(
                new FileEntry(
                    Path: ToRelativePath(fullPath),
                    IsDirectory: false,
                    Size: info.Length,
                    LastModifiedUtc: info.LastWriteTimeUtc
                )
            );
        }

        if (Directory.Exists(fullPath))
        {
            var info = new DirectoryInfo(fullPath);
            return Task.FromResult<FileEntry?>(
                new FileEntry(
                    Path: ToRelativePath(fullPath),
                    IsDirectory: true,
                    Size: 0,
                    LastModifiedUtc: info.LastWriteTimeUtc
                )
            );
        }

        return Task.FromResult<FileEntry?>(null);
    }

    public Task<string> ReadAllTextAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolvePath(path);
        return File.ReadAllTextAsync(fullPath, ct);
    }

    public Task<string[]> ReadAllLinesAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolvePath(path);
        return File.ReadAllLinesAsync(fullPath, ct);
    }

    public async Task WriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        using var pathLock = await LocalFilePathLocks.AcquireAsync(fullPath, ct).ConfigureAwait(false);
        await WriteFileAsync(fullPath, content, ct).ConfigureAwait(false);
    }

    public async Task<bool> CreateTextFileAsync(string path, string content, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        using var pathLock = await LocalFilePathLocks.AcquireAsync(fullPath, ct).ConfigureAwait(false);
        if (File.Exists(fullPath))
        {
            return false;
        }

        await WriteFileAsync(fullPath, content, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<string>> ReadLinesAsync(
        string path,
        int startLine,
        int? endLine,
        CancellationToken ct
    )
    {
        var fullPath = ResolveFilePath(path);
        var content = await ReadExistingFileAsync(path, fullPath, ct).ConfigureAwait(false);
        return TextContentEditor.SliceLines(content, startLine, endLine);
    }

    public async Task<int> ReplaceTextAsync(
        string path,
        string oldString,
        string newString,
        bool replaceAll,
        CancellationToken ct
    )
    {
        var fullPath = ResolveFilePath(path);
        using var pathLock = await LocalFilePathLocks.AcquireAsync(fullPath, ct).ConfigureAwait(false);
        var content = await ReadExistingFileAsync(path, fullPath, ct).ConfigureAwait(false);
        var (newContent, count) = TextContentEditor.ApplyReplace(content, oldString, newString, replaceAll);
        await WriteFileAsync(fullPath, newContent, ct).ConfigureAwait(false);
        return count;
    }

    public async Task ReplaceLinesAsync(string path, IReadOnlyList<AgwFileLineEdit> edits, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        using var pathLock = await LocalFilePathLocks.AcquireAsync(fullPath, ct).ConfigureAwait(false);
        var content = await ReadExistingFileAsync(path, fullPath, ct).ConfigureAwait(false);
        var newContent = TextContentEditor.ApplyReplaceLines(content, edits);
        await WriteFileAsync(fullPath, newContent, ct).ConfigureAwait(false);
    }

    public IAgwFileSystem GetSubFileSystem(string path) => new LocalFileSystem(ResolvePath(path));

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolvePath(path);
        Directory.CreateDirectory(fullPath);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolvePath(path);
        using var pathLock = await LocalFilePathLocks.AcquireAsync(fullPath, ct).ConfigureAwait(false);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
        else if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(
        string path,
        string searchPattern,
        bool recursive,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        var fullPath = ResolvePath(path);
        if (!Directory.Exists(fullPath))
        {
            yield break;
        }

        // 类型、大小与修改时间直接读取枚举得到的 FileSystemEntry，每个条目不再额外调用文件系统。
        // Type, size and modification time come from the enumerated FileSystemEntry, with no extra file system call per entry.
        var ignoreCase = PathComparison == StringComparison.OrdinalIgnoreCase;
        var entries = new FileSystemEnumerable<FileEntry>(
            fullPath,
            (ref FileSystemEntry entry) =>
                new FileEntry(
                    Path: ToRelativePath(entry.ToFullPath()),
                    IsDirectory: entry.IsDirectory,
                    Size: entry.IsDirectory ? 0 : entry.Length,
                    LastModifiedUtc: entry.LastWriteTimeUtc
                ),
            new EnumerationOptions
            {
                AttributesToSkip = recursive ? FileAttributes.ReparsePoint : (FileAttributes)0,
                IgnoreInaccessible = true,
                RecurseSubdirectories = recursive,
                ReturnSpecialDirectories = false,
            }
        )
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                FileSystemName.MatchesSimpleExpression(searchPattern, entry.FileName, ignoreCase),
        };

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    public async IAsyncEnumerable<AgwFileSearchResult> SearchAsync(
        string rootPath,
        SearchOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        var fullPath = ResolvePath(rootPath);
        if (!Directory.Exists(fullPath))
        {
            yield break;
        }

        // 目录名直接用 ReadOnlySpan<char> 查找，每个条目不再分配字符串。
        // Directory names are looked up with ReadOnlySpan<char>, allocating no string per entry.
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>>? excludedDirectoryNames = null;
        if (options.ExcludedDirectoryNames is { Count: > 0 })
        {
            excludedDirectoryNames = new HashSet<string>(
                options.ExcludedDirectoryNames,
                StringComparer.OrdinalIgnoreCase
            ).GetAlternateLookup<ReadOnlySpan<char>>();
        }

        var candidates = EnumerateSearchFiles(fullPath, options.Recursive, excludedDirectoryNames, ct)
            .Select(file => new SearchCandidate(
                ToRelativePath(file.FullPath),
                Path.GetRelativePath(fullPath, file.FullPath).Replace(Path.DirectorySeparatorChar, '/'),
                file.Length,
                () => new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read)
            ));
        await foreach (var result in FileContentSearch.SearchAsync(candidates, options, ct).ConfigureAwait(false))
        {
            yield return result;
        }
    }

    private static IEnumerable<SearchFile> EnumerateSearchFiles(
        string rootPath,
        bool recursive,
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>>? excludedDirectoryNames,
        CancellationToken cancellationToken
    )
    {
        var files = new FileSystemEnumerable<SearchFile>(
            rootPath,
            static (ref FileSystemEntry entry) => new SearchFile(entry.ToFullPath(), entry.Length),
            new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
                RecurseSubdirectories = recursive,
                ReturnSpecialDirectories = false,
            }
        )
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
        };

        if (excludedDirectoryNames is { } excluded)
        {
            files.ShouldRecursePredicate = (ref FileSystemEntry entry) => !excluded.Contains(entry.FileName);
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    private static async Task WriteFileAsync(string fullPath, string content, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, content, ct).ConfigureAwait(false);
    }

    private static Task<string> ReadExistingFileAsync(string path, string fullPath, CancellationToken ct)
    {
        if (!File.Exists(fullPath))
        {
            throw new AgwException(ErrorCodes.FileNotFound, $"File '{path}' was not found.");
        }

        return File.ReadAllTextAsync(fullPath, ct);
    }

    /// <summary>
    /// 枚举得到的待搜索文件：完整路径与枚举时读到的长度。
    /// A file to search from the enumeration: its full path and the length read while enumerating.
    /// </summary>
    private readonly record struct SearchFile(string FullPath, long Length);
}
