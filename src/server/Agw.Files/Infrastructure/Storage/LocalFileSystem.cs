using System.IO.Enumeration;
using System.Runtime.Versioning;
using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Files.Infrastructure.Storage.Confined;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Agw.Files.Infrastructure.Storage;

public sealed class LocalFileSystem : ILocalFileSystem
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly string _rootPath;
    private readonly string _rootFullPath;
    private readonly string _normalizedRoot;
    private readonly FilePathLocks _pathLocks;
    private readonly ILogger _logger;

    public LocalFileSystem(string rootPath, FilePathLocks pathLocks, ILogger logger)
    {
        _rootPath = rootPath;
        _rootFullPath = Path.GetFullPath(rootPath);
        _normalizedRoot =
            _rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        _pathLocks = pathLocks;
        _logger = logger;
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

    public Task<bool> IsSymbolicLinkAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(GetLinkTarget(ResolvePath(path)) is not null);
    }

    public Task<bool?> GetSymbolicLinkDirectoryAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = ResolvePath(path);
        if (Directory.Exists(fullPath))
        {
            return Task.FromResult<bool?>(true);
        }

        return Task.FromResult<bool?>(File.Exists(fullPath) ? false : null);
    }

    public IAgwFileSystem Confine(IReadOnlyList<string> allowedRoots) =>
        ConfinedFileSystem.Create(_rootFullPath, allowedRoots, _pathLocks, _logger);

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
        await using var pathLock = await _pathLocks.AcquireContentAsync(fullPath, ct).ConfigureAwait(false);
        await WriteFileAsync(fullPath, content, ct).ConfigureAwait(false);
    }

    public async Task<bool> CreateTextFileAsync(string path, string content, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        await using var pathLock = await _pathLocks.AcquireContentAsync(fullPath, ct).ConfigureAwait(false);
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
        await using var pathLock = await _pathLocks.AcquireContentAsync(fullPath, ct).ConfigureAwait(false);
        var content = await ReadExistingFileAsync(path, fullPath, ct).ConfigureAwait(false);
        var (newContent, count) = TextContentEditor.ApplyReplace(content, oldString, newString, replaceAll);
        await WriteFileAsync(fullPath, newContent, ct).ConfigureAwait(false);
        return count;
    }

    public async Task ReplaceLinesAsync(string path, IReadOnlyList<AgwFileLineEdit> edits, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        await using var pathLock = await _pathLocks.AcquireContentAsync(fullPath, ct).ConfigureAwait(false);
        var content = await ReadExistingFileAsync(path, fullPath, ct).ConfigureAwait(false);
        var newContent = TextContentEditor.ApplyReplaceLines(content, edits);
        await WriteFileAsync(fullPath, newContent, ct).ConfigureAwait(false);
    }

    public IAgwFileSystem GetSubFileSystem(string path) => new LocalFileSystem(ResolvePath(path), _pathLocks, _logger);

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolvePath(path);
        await using var pathLock = await _pathLocks.AcquireEntryAsync(fullPath, ct).ConfigureAwait(false);
        Directory.CreateDirectory(fullPath);
    }

    public async Task DeleteAsync(string path, bool recursive, CancellationToken ct)
    {
        var fullPath = ResolvePath(path);
        await using var pathLock = await _pathLocks.AcquireEntryAsync(fullPath, ct).ConfigureAwait(false);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
        else if (Directory.Exists(fullPath))
        {
            // 非递归时由 Directory.Delete 只移除空目录，非空目录的系统错误转换为 DirectoryNotEmpty。
            // When not recursive, Directory.Delete removes only an empty directory, and the system error for a non-empty
            // directory becomes DirectoryNotEmpty.
            try
            {
                Directory.Delete(fullPath, recursive);
            }
            catch (IOException exception) when (IsDirectoryNotEmpty(exception))
            {
                throw new AgwException(ErrorCodes.DirectoryNotEmpty, $"Directory '{path}' is not empty.", exception);
            }
        }
        else if (GetLinkTarget(fullPath) is not null)
        {
            if (OperatingSystem.IsWindows() && (File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
            {
                Directory.Delete(fullPath, recursive);
            }
            else
            {
                File.Delete(fullPath);
            }
        }
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        if (!File.Exists(fullPath))
        {
            throw new AgwException(ErrorCodes.FileNotFound, $"File '{path}' was not found.");
        }

        return Task.FromResult<Stream>(
            new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
        );
    }

    public async Task WriteAsync(
        string path,
        Stream content,
        bool overwrite,
        bool createParents,
        string? expectedSha256,
        CancellationToken ct
    )
    {
        var fullPath = ResolveFilePath(path);
        await using var pathLock = await _pathLocks.AcquireContentAsync(fullPath, ct).ConfigureAwait(false);
        if (Directory.Exists(fullPath))
        {
            throw overwrite
                ? new AgwException(ErrorCodes.InvalidParam, $"'{path}' is not a regular file.")
                : new AgwException(ErrorCodes.FileAlreadyExists, $"'{path}' already exists.");
        }

        if (!overwrite && File.Exists(fullPath))
        {
            throw new AgwException(ErrorCodes.FileAlreadyExists, $"'{path}' already exists.");
        }

        var directory = Path.GetDirectoryName(fullPath)!;
        if (createParents)
        {
            Directory.CreateDirectory(directory);
        }
        else if (!Directory.Exists(directory))
        {
            throw new AgwException(ErrorCodes.DirectoryNotFound, $"The parent directory of '{path}' was not found.");
        }

        // 目标是符号链接时，临时文件与重命名都针对链接的最终目标，链接本身保持不变。
        // When the target is a symbolic link, the temporary file and the rename both apply to the link's final target,
        // and the link itself stays unchanged.
        var file = new FileInfo(fullPath);
        var target = file.LinkTarget == null ? fullPath : file.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
        if (OperatingSystem.IsWindows())
        {
            await WriteWindowsAsync(path, target, content, overwrite, expectedSha256, pathLock, ct)
                .ConfigureAwait(false);
        }
        else
        {
            await WriteUnixAsync(path, target, content, overwrite, expectedSha256, pathLock, ct).ConfigureAwait(false);
        }
    }

    public async Task MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken ct)
    {
        var sourceFullPath = ResolveFilePath(sourcePath);
        var destinationFullPath = ResolveFilePath(destinationPath);
        await using var pathLock = await _pathLocks
            .AcquireEntryAsync(sourceFullPath, destinationFullPath, ct)
            .ConfigureAwait(false);

        var sourceIsDirectory = Directory.Exists(sourceFullPath);
        var sourceIsSymbolicLink = GetLinkTarget(sourceFullPath) is not null;
        if (!File.Exists(sourceFullPath) && !sourceIsDirectory && !sourceIsSymbolicLink)
        {
            throw new AgwException(ErrorCodes.FileNotFound, $"'{sourcePath}' was not found.");
        }

        if (!Directory.Exists(Path.GetDirectoryName(destinationFullPath)))
        {
            throw new AgwException(
                ErrorCodes.DirectoryNotFound,
                $"The parent directory of '{destinationPath}' was not found."
            );
        }

        var destinationIsDirectory = Directory.Exists(destinationFullPath);
        var destinationIsSymbolicLink = GetLinkTarget(destinationFullPath) is not null;
        var destinationIsDirectoryEntry =
            destinationIsDirectory || IsDirectorySymbolicLink(destinationFullPath, destinationIsSymbolicLink);
        var destinationIsFileEntry =
            File.Exists(destinationFullPath) || (destinationIsSymbolicLink && !destinationIsDirectoryEntry);
        var sourceIsDirectoryEntry = sourceIsDirectory || IsDirectorySymbolicLink(sourceFullPath, sourceIsSymbolicLink);
        if (destinationIsDirectoryEntry || (destinationIsFileEntry && (!overwrite || sourceIsDirectoryEntry)))
        {
            throw new AgwException(ErrorCodes.FileAlreadyExists, $"'{destinationPath}' already exists.");
        }

        if (!sourceIsDirectoryEntry)
        {
            File.Move(sourceFullPath, destinationFullPath, overwrite);
        }
        else
        {
            Directory.Move(sourceFullPath, destinationFullPath);
        }
    }

    private static string? GetLinkTarget(string path) =>
        new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;

    private static bool IsDirectorySymbolicLink(string path, bool isSymbolicLink) =>
        OperatingSystem.IsWindows() && isSymbolicLink && (File.GetAttributes(path) & FileAttributes.Directory) != 0;

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

    /// <summary>
    /// Unix 上的原子提交：哈希检查、临时文件、复制与刷新、复核与重命名都以目标所在目录的句柄为基准。
    /// The atomic commit on Unix: the hash check, temporary file, copy and flush, re-check and rename are all relative
    /// to the handle of the target's directory.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private async Task WriteUnixAsync(
        string path,
        string target,
        Stream content,
        bool overwrite,
        string? expectedSha256,
        FilePathLease pathLock,
        CancellationToken ct
    )
    {
        var directoryPath = Path.GetDirectoryName(target)!;
        var name = Path.GetFileName(target);
        using var directory = UnixAtomicCommit.OpenDirectory(directoryPath, path);
        FileVersion? expected = null;
        UnixFileMode? permissions = null;
        if (expectedSha256 != null)
        {
            using var existing =
                UnixAtomicCommit.OpenExisting(directory, name, path) ?? throw AtomicFileWrite.FileChanged(path);
            var status = UnixNative.Stat(existing);
            expected = status.Version;
            permissions = status.Permissions;
            var actualSha256 = await AtomicFileWrite.HashAsync(existing, ct).ConfigureAwait(false);
            AtomicFileWrite.VerifyHash(expectedSha256, actualSha256, path);
        }
        else if (File.Exists(target))
        {
            permissions = File.GetUnixFileMode(target) & (UnixFileMode)UnixNative.PermissionMask;
        }

        // 此时持有该文件的锁，同一目录中这个文件以前留下的临时文件都不再使用。
        // The file's lock is held now, so earlier temporary files of this file in the directory are no longer in use.
        foreach (var stale in AtomicFileWrite.FindTemporaryFiles(directoryPath, name))
        {
            UnixAtomicCommit.DeleteTemporary(directory, stale);
        }

        await UnixAtomicCommit
            .WriteAsync(directory, name, content, permissions, overwrite, expected, pathLock, path, target, _logger)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Windows 上的原子提交：临时文件以 CreateNew 创建，提交使用 File.Move；不替换时 MoveFileEx 不带
    /// MOVEFILE_REPLACE_EXISTING，目标已存在时原子地失败。
    /// The atomic commit on Windows: the temporary file is created with CreateNew and committed with File.Move; without
    /// overwrite MoveFileEx runs without MOVEFILE_REPLACE_EXISTING and fails atomically when the target exists.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task WriteWindowsAsync(
        string path,
        string target,
        Stream content,
        bool overwrite,
        string? expectedSha256,
        FilePathLease pathLock,
        CancellationToken ct
    )
    {
        var directoryPath = Path.GetDirectoryName(target)!;
        var name = Path.GetFileName(target);
        FileVersion? expected = null;
        if (expectedSha256 != null)
        {
            using var existing =
                OpenWindowsTarget(target, WindowsNative.GENERIC_READ, path) ?? throw AtomicFileWrite.FileChanged(path);
            expected = WindowsNative.GetFileVersion(existing);
            var actualSha256 = await AtomicFileWrite.HashAsync(existing, ct).ConfigureAwait(false);
            AtomicFileWrite.VerifyHash(expectedSha256, actualSha256, path);
        }

        // 此时持有该文件的锁，同一目录中这个文件以前留下的临时文件都不再使用。
        // The file's lock is held now, so earlier temporary files of this file in the directory are no longer in use.
        foreach (var stale in AtomicFileWrite.FindTemporaryFiles(directoryPath, name))
        {
            File.Delete(Path.Combine(directoryPath, stale));
        }

        var temporaryName = AtomicFileWrite.CreateTemporaryName(name);
        var temporaryPath = Path.Combine(directoryPath, temporaryName);
        var created = false;
        var committed = false;
        try
        {
            using (var temporary = File.OpenHandle(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                await AtomicFileWrite.CopyAsync(content, temporary, pathLock.Token).ConfigureAwait(false);
            }

            CommitWindows(path, temporaryPath, target, overwrite, expected, pathLock);
            committed = true;
        }
        finally
        {
            if (created && !committed)
            {
                AtomicFileWrite.DeleteTemporaryFile(() => File.Delete(temporaryPath), target, temporaryName, _logger);
            }
        }
    }

    /// <summary>
    /// 复核并提交：复核与重命名在同一线程中连续执行，中间没有 await。目标是只读文件时 MoveFileEx 以 ERROR_ACCESS_DENIED
    /// 失败（UnauthorizedAccessException），只读文件不被替换，抛出 <c>FileNotWritable</c>。
    /// Re-checks and commits: the re-check and the rename run back to back on one thread with no await in between. A
    /// read-only target makes MoveFileEx fail with ERROR_ACCESS_DENIED (UnauthorizedAccessException), so a read-only
    /// file is never replaced and <c>FileNotWritable</c> is thrown.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void CommitWindows(
        string path,
        string temporaryPath,
        string target,
        bool overwrite,
        FileVersion? expected,
        FilePathLease pathLock
    )
    {
        pathLock.ThrowIfLost();
        if (expected is { } version)
        {
            using var current =
                OpenWindowsTarget(target, WindowsNative.FILE_READ_ATTRIBUTES, path)
                ?? throw AtomicFileWrite.FileChanged(path);
            AtomicFileWrite.VerifyVersion(version, WindowsNative.GetFileVersion(current), path);
        }

        try
        {
            File.Move(temporaryPath, target, overwrite);
        }
        catch (IOException exception) when (!overwrite && IsAlreadyExists(exception))
        {
            throw new AgwException(ErrorCodes.FileAlreadyExists, $"'{path}' already exists.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AgwException(ErrorCodes.FileNotWritable, $"'{path}' is read-only or not writable.", exception);
        }
    }

    /// <summary>
    /// 打开已有的目标文件；不存在时返回 null。
    /// Opens the existing target file; returns null when it does not exist.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static SafeFileHandle? OpenWindowsTarget(string target, uint access, string path)
    {
        var handle = WindowsNative.CreateFile(
            target,
            access,
            WindowsNative.ShareAll,
            IntPtr.Zero,
            WindowsNative.OPEN_EXISTING,
            0,
            IntPtr.Zero
        );
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = WindowsNative.LastError;
        handle.Dispose();
        return error is WindowsNative.ERROR_FILE_NOT_FOUND or WindowsNative.ERROR_PATH_NOT_FOUND
            ? null
            : throw WindowsNative.CreateException(error, $"Cannot open '{path}'");
    }

    /// <summary>
    /// 判断 File.Move 的 IOException 是否表示目标已存在：HResult 为 ERROR_ALREADY_EXISTS 或 ERROR_FILE_EXISTS 对应的
    /// HRESULT。
    /// Decides whether an IOException from File.Move means the target exists: its HResult is the HRESULT of
    /// ERROR_ALREADY_EXISTS or ERROR_FILE_EXISTS.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool IsAlreadyExists(IOException exception) =>
        exception.HResult == unchecked((int)0x80070000 | WindowsNative.ERROR_ALREADY_EXISTS)
        || exception.HResult == unchecked((int)0x80070000 | WindowsNative.ERROR_FILE_EXISTS);

    /// <summary>
    /// 判断删除目录得到的 IOException 是否表示目录非空：Unix 上 HResult 是原始 errno（ENOTEMPTY），Windows 上是
    /// ERROR_DIR_NOT_EMPTY 对应的 HRESULT。
    /// Decides whether an IOException from deleting a directory means the directory is not empty: on Unix the HResult
    /// is the raw errno (ENOTEMPTY), on Windows the HRESULT of ERROR_DIR_NOT_EMPTY.
    /// </summary>
    private static bool IsDirectoryNotEmpty(IOException exception) =>
        OperatingSystem.IsWindows()
            ? exception.HResult == unchecked((int)0x80070000 | WindowsNative.ERROR_DIR_NOT_EMPTY)
            : exception.HResult == UnixNative.ENOTEMPTY;

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
