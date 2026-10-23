using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Agw.Files.Infrastructure.Storage.Confined;

/// <summary>
/// Windows 上的受限文件系统。每个操作先按路径打开句柄，再用 GetFinalPathNameByHandle 读取该句柄的最终物理路径，只有它位于
/// 允许的目录内才继续，之后的 IO 全部使用这个句柄；新建文件时若句柄落在允许范围之外，通过句柄标记删除后再拒绝。目录枚举以
/// 已校验目录句柄的物理路径为起点，枚举得到的每个文件在打开时再次按句柄校验。
/// The confined file system on Windows. Every operation opens a handle by path, reads that handle's final physical path
/// with GetFinalPathNameByHandle, continues only when it lies inside an allowed directory, and performs all IO through
/// the handle; a newly created file whose handle lands outside is marked for deletion through the handle before the
/// operation is rejected. Directory enumeration starts from the physical path of a verified directory handle, and every
/// enumerated file is verified again through its own handle when opened.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsConfinedFileSystem : IAgwFileSystem
{
    private readonly LocalFileSystem _root;
    private readonly string _prefix;
    private readonly LocalFileSystem _inner;
    private readonly string[] _allowedRoots;
    private readonly FilePathLocks _pathLocks;
    private readonly ILogger _logger;

    /// <param name="root">Project 根目录的词法文件系统。The lexical file system of the Project root.</param>
    /// <param name="prefix">
    /// 本实例相对 Project 根目录的子目录（子文件系统），空字符串表示根目录本身；目录创建从根目录开始，因此子目录本身也会被创建。
    /// The subdirectory of this instance relative to the Project root (a sub file system), empty for the root itself;
    /// directory creation starts from the root, so the subdirectory itself is created as well.
    /// </param>
    /// <param name="allowedRoots">允许的目录的最终物理路径。The final physical paths of the allowed directories.</param>
    /// <param name="pathLocks">所有写入路径共用的文件锁。The file locks shared by every write path.</param>
    /// <param name="logger">记录临时文件删除失败。Logs failures to delete temporary files.</param>
    private WindowsConfinedFileSystem(
        LocalFileSystem root,
        string prefix,
        string[] allowedRoots,
        FilePathLocks pathLocks,
        ILogger logger
    )
    {
        _root = root;
        _prefix = prefix;
        _inner = prefix.Length == 0 ? root : (LocalFileSystem)root.GetSubFileSystem(prefix);
        _allowedRoots = allowedRoots;
        _pathLocks = pathLocks;
        _logger = logger;
    }

    public static WindowsConfinedFileSystem Create(
        string rootFullPath,
        IReadOnlyList<string> allowedRoots,
        FilePathLocks pathLocks,
        ILogger logger
    )
    {
        var roots = new List<string>(allowedRoots.Count);
        foreach (var root in allowedRoots)
        {
            using var handle = WindowsNative.CreateFile(
                root,
                WindowsNative.FILE_READ_ATTRIBUTES,
                WindowsNative.ShareAll,
                IntPtr.Zero,
                WindowsNative.OPEN_EXISTING,
                WindowsNative.FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero
            );
            if (handle.IsInvalid)
            {
                var error = WindowsNative.LastError;
                if (error is WindowsNative.ERROR_FILE_NOT_FOUND or WindowsNative.ERROR_PATH_NOT_FOUND)
                {
                    continue;
                }

                throw WindowsNative.CreateException(error, $"Cannot open Project directory '{root}'");
            }

            roots.Add(WindowsNative.GetFinalPath(handle));
        }

        return new WindowsConfinedFileSystem(
            new LocalFileSystem(rootFullPath, pathLocks, logger),
            string.Empty,
            roots.ToArray(),
            pathLocks,
            logger
        );
    }

    public Task<bool> ExistsFileAsync(string path, CancellationToken ct)
    {
        using var opened = OpenExisting(
            path,
            WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS
        );
        return Task.FromResult(opened != null && !opened.IsDirectory);
    }

    public Task<bool> ExistsDirectoryAsync(string path, CancellationToken ct)
    {
        using var opened = OpenExisting(
            path,
            WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS
        );
        return Task.FromResult(opened is { IsDirectory: true });
    }

    public Task<FileEntry?> StatAsync(string path, CancellationToken ct)
    {
        using var opened = OpenExisting(
            path,
            WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS
        );
        if (opened == null)
        {
            return Task.FromResult<FileEntry?>(null);
        }

        return Task.FromResult<FileEntry?>(
            new FileEntry(
                RelativePath(path),
                opened.IsDirectory,
                opened.IsDirectory ? 0 : RandomAccess.GetLength(opened.Handle),
                File.GetLastWriteTimeUtc(opened.Handle)
            )
        );
    }

    public async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
    {
        using var opened = OpenExistingFile(path, WindowsNative.GENERIC_READ);
        return await HandleTextIO.ReadAllTextAsync(opened.Handle, ct).ConfigureAwait(false);
    }

    public async Task<string[]> ReadAllLinesAsync(string path, CancellationToken ct)
    {
        using var opened = OpenExistingFile(path, WindowsNative.GENERIC_READ);
        var content = await HandleTextIO.ReadAllTextAsync(opened.Handle, ct).ConfigureAwait(false);
        using var reader = new StringReader(content);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    public async Task WriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        await using var pathLock = await _pathLocks.AcquireContentAsync(fullPath, ct).ConfigureAwait(false);
        EnsureParentDirectories(path);
        using var opened = OpenOrCreateFile(path, mustCreate: false)!;
        await HandleTextIO.WriteAllTextAsync(opened.Handle, content, ct).ConfigureAwait(false);
    }

    public async Task<bool> CreateTextFileAsync(string path, string content, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        await using var pathLock = await _pathLocks.AcquireContentAsync(fullPath, ct).ConfigureAwait(false);
        EnsureParentDirectories(path);
        using var opened = OpenOrCreateFile(path, mustCreate: true);
        if (opened == null)
        {
            return false;
        }

        await HandleTextIO.WriteAllTextAsync(opened.Handle, content, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<string>> ReadLinesAsync(
        string path,
        int startLine,
        int? endLine,
        CancellationToken ct
    )
    {
        ResolveFilePath(path);
        using var opened = OpenExistingFile(path, WindowsNative.GENERIC_READ);
        var content = await HandleTextIO.ReadAllTextAsync(opened.Handle, ct).ConfigureAwait(false);
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
        using var opened = OpenExistingFile(path, WindowsNative.GENERIC_READ | WindowsNative.GENERIC_WRITE);
        var content = await HandleTextIO.ReadAllTextAsync(opened.Handle, ct).ConfigureAwait(false);
        var (newContent, count) = TextContentEditor.ApplyReplace(content, oldString, newString, replaceAll);
        await HandleTextIO.WriteAllTextAsync(opened.Handle, newContent, ct).ConfigureAwait(false);
        return count;
    }

    public async Task ReplaceLinesAsync(string path, IReadOnlyList<AgwFileLineEdit> edits, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        await using var pathLock = await _pathLocks.AcquireContentAsync(fullPath, ct).ConfigureAwait(false);
        using var opened = OpenExistingFile(path, WindowsNative.GENERIC_READ | WindowsNative.GENERIC_WRITE);
        var content = await HandleTextIO.ReadAllTextAsync(opened.Handle, ct).ConfigureAwait(false);
        var newContent = TextContentEditor.ApplyReplaceLines(content, edits);
        await HandleTextIO.WriteAllTextAsync(opened.Handle, newContent, ct).ConfigureAwait(false);
    }

    public IAgwFileSystem GetSubFileSystem(string path) =>
        new WindowsConfinedFileSystem(_root, RootRelativePath(path), _allowedRoots, _pathLocks, _logger);

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolveLexicalPath(path);
        await using var pathLock = await _pathLocks.AcquireEntryAsync(fullPath, ct).ConfigureAwait(false);
        EnsureDirectories(RootRelativePath(path));
    }

    public async Task DeleteAsync(string path, bool recursive, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        await using var pathLock = await _pathLocks.AcquireEntryAsync(fullPath, ct).ConfigureAwait(false);
        Delete(path, recursive, ct);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var opened = OpenExistingFile(path, WindowsNative.GENERIC_READ);
        try
        {
            return Task.FromResult<Stream>(new FileStream(opened.Handle, FileAccess.Read));
        }
        catch
        {
            opened.Dispose();
            throw;
        }
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
        if (createParents)
        {
            EnsureParentDirectories(path);
        }
        else
        {
            OpenParentDirectory(path).Dispose();
        }

        var (directoryPath, name) = LocateWriteTarget(path, overwrite);
        // 目标所在目录的句柄请求 FILE_TRAVERSE，共享方式不含 FILE_SHARE_DELETE，持有到提交完成；只请求属性的句柄不参与共享
        // 检查，请求 FILE_TRAVERSE 后共享方式才生效，期间该目录不能被重命名、删除或替换为 junction。
        // The handle of the target's directory requests FILE_TRAVERSE with a share mode that lacks FILE_SHARE_DELETE
        // and is held until the commit completes; a handle requesting only attributes takes no part in share checks,
        // and with FILE_TRAVERSE the share mode applies, so the directory cannot be renamed, deleted or replaced by a
        // junction meanwhile.
        using var parent = OpenCommitDirectory(directoryPath, path);
        FileVersion? expected = null;
        if (expectedSha256 != null)
        {
            // 哈希读取的就是提交时被替换的文件：提交目录中的这个名称。
            // The hash reads exactly the file the commit replaces: this name in the commit directory.
            using var readable =
                OpenCommitTarget(parent, name, WindowsNative.GENERIC_READ, path)
                ?? throw AtomicFileWrite.FileChanged(path);
            expected = WindowsNative.GetFileVersion(readable.Handle);
            var actualSha256 = await AtomicFileWrite.HashAsync(readable.Handle, ct).ConfigureAwait(false);
            AtomicFileWrite.VerifyHash(expectedSha256, actualSha256, path);
        }

        DeleteStaleTemporaryFiles(parent, name);
        await CommitWriteAsync(parent, name, content, overwrite, expected, pathLock, path).ConfigureAwait(false);
    }

    public async Task MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken ct)
    {
        var sourceFullPath = ResolveFilePath(sourcePath);
        var destinationFullPath = ResolveFilePath(destinationPath);
        await using var pathLock = await _pathLocks
            .AcquireEntryAsync(sourceFullPath, destinationFullPath, ct)
            .ConfigureAwait(false);
        // 源带 OPEN_REPARSE_POINT 打开，链接与 junction 移动的是其本身；目标位置相对已校验的父目录句柄解析。
        // The source is opened with OPEN_REPARSE_POINT so a link or junction is moved itself; the destination is
        // resolved relative to the verified parent directory handle.
        using var source =
            OpenExisting(
                sourcePath,
                WindowsNative.DELETE | WindowsNative.FILE_READ_ATTRIBUTES,
                WindowsNative.FILE_FLAG_BACKUP_SEMANTICS | WindowsNative.FILE_FLAG_OPEN_REPARSE_POINT
            ) ?? throw new AgwException(ErrorCodes.FileNotFound, $"'{sourcePath}' was not found.");
        using var parent = OpenParentDirectory(destinationPath);
        using (
            var existing = OpenExisting(
                destinationPath,
                WindowsNative.FILE_READ_ATTRIBUTES,
                WindowsNative.FILE_FLAG_BACKUP_SEMANTICS | WindowsNative.FILE_FLAG_OPEN_REPARSE_POINT
            )
        )
        {
            if (existing != null && (!overwrite || source.IsDirectory || existing.IsDirectory))
            {
                throw new AgwException(ErrorCodes.FileAlreadyExists, $"'{destinationPath}' already exists.");
            }
        }

        var destination = RelativePath(destinationPath);
        var error = WindowsNative.Rename(
            source.Handle,
            parent.Handle,
            destination[(destination.LastIndexOf('/') + 1)..],
            replace: overwrite && !source.IsDirectory
        );
        if (error == 0)
        {
            return;
        }

        throw error is WindowsNative.ERROR_ALREADY_EXISTS or WindowsNative.ERROR_FILE_EXISTS
            ? new AgwException(ErrorCodes.FileAlreadyExists, $"'{destinationPath}' already exists.")
            : WindowsNative.CreateException(error, $"Cannot move '{sourcePath}' to '{destinationPath}'");
    }

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(
        string path,
        string searchPattern,
        bool recursive,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        using var directory = OpenExisting(
            path,
            WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS
        );
        if (directory is not { IsDirectory: true })
        {
            yield break;
        }

        var directoryPath = RelativePath(path);
        var entries = new FileSystemEnumerable<(
            string Path,
            bool IsDirectory,
            bool IsLink,
            long Size,
            DateTimeOffset Modified
        )>(
            directory.FinalPath,
            (ref FileSystemEntry entry) =>
                (
                    Combine(directoryPath, ToRelativePath(directory.FinalPath, entry.ToFullPath())),
                    entry.IsDirectory,
                    (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                    entry.IsDirectory ? 0 : entry.Length,
                    entry.LastWriteTimeUtc
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
                FileSystemName.MatchesSimpleExpression(searchPattern, entry.FileName, ignoreCase: true),
        };

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.IsLink)
            {
                yield return new FileEntry(entry.Path, entry.IsDirectory, entry.Size, entry.Modified);
                continue;
            }

            // 非递归枚举时，链接按其在允许目录内的目标展示；目标在外部或不存在时不展示。
            // In a non-recursive listing a link is shown as its target inside the allowed directories, and is hidden
            // when the target is outside or missing.
            FileEntry? linked;
            try
            {
                linked = await StatAsync(entry.Path, ct).ConfigureAwait(false);
            }
            catch (AgwException exception) when (exception.Code == ErrorCodes.FilePathOutsideRoot.Code)
            {
                linked = null;
            }

            if (linked != null)
            {
                yield return linked;
            }
        }
    }

    public async IAsyncEnumerable<AgwFileSearchResult> SearchAsync(
        string rootPath,
        SearchOptions options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        using var directory = OpenExisting(
            rootPath,
            WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS
        );
        if (directory is not { IsDirectory: true })
        {
            yield break;
        }

        HashSet<string>? excludedDirectoryNames = options.ExcludedDirectoryNames is { Count: > 0 }
            ? new HashSet<string>(options.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase)
            : null;
        var searchRoot = RelativePath(rootPath);
        var files = new FileSystemEnumerable<(string FullPath, long Length)>(
            directory.FinalPath,
            static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Length),
            new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
                RecurseSubdirectories = options.Recursive,
                ReturnSpecialDirectories = false,
            }
        )
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
        };
        if (excludedDirectoryNames != null)
        {
            files.ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                !excludedDirectoryNames.Contains(entry.FileName.ToString());
        }

        var candidates = files.Select(file =>
        {
            var searchRelativePath = ToRelativePath(directory.FinalPath, file.FullPath);
            return new SearchCandidate(
                Combine(searchRoot, searchRelativePath),
                searchRelativePath,
                file.Length,
                () =>
                    new FileStream(
                        OpenVerifiedAbsolute(file.FullPath, WindowsNative.GENERIC_READ).Handle,
                        FileAccess.Read
                    )
            );
        });
        await foreach (var result in FileContentSearch.SearchAsync(candidates, options, ct).ConfigureAwait(false))
        {
            yield return result;
        }
    }

    /// <summary>
    /// 检查写入目标并返回提交所在的目录与名称。目标已存在时使用它最终物理路径中的目录与名称：经过符号链接时替换的是链接
    /// 指向的文件，链接本身保持不变。最后一段是指向不存在目标的符号链接时，使用链接最终目标的目录与名称，同样不替换链接
    /// 本身；提交目录随后按句柄校验是否在允许的目录内。其余情况使用已校验父目录的物理路径与请求中的文件名。
    /// Checks the write target and returns the directory and name of the commit. For an existing target these come from
    /// its final physical path, so through a symbolic link the file it points to is replaced and the link itself stays
    /// unchanged. When the last segment is a symbolic link to a missing target, the directory and name of the link's
    /// final target are used, so the link itself is not replaced either; the commit directory is then verified through
    /// its handle to lie inside the allowed directories. Otherwise they are the verified parent's physical path and the
    /// requested file name.
    /// </summary>
    private (string DirectoryPath, string Name) LocateWriteTarget(string path, bool overwrite)
    {
        using var existing = OpenExisting(
            path,
            WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS
        );
        if (existing is { IsDirectory: true })
        {
            throw overwrite
                ? new AgwException(ErrorCodes.InvalidParam, $"'{path}' is not a regular file.")
                : AtomicFileWrite.AlreadyExists(path);
        }

        if (existing != null)
        {
            return overwrite
                ? (Path.GetDirectoryName(existing.FinalPath)!, Path.GetFileName(existing.FinalPath))
                : throw AtomicFileWrite.AlreadyExists(path);
        }

        using (
            var link = OpenExisting(
                path,
                WindowsNative.FILE_READ_ATTRIBUTES,
                WindowsNative.FILE_FLAG_BACKUP_SEMANTICS | WindowsNative.FILE_FLAG_OPEN_REPARSE_POINT
            )
        )
        {
            if (link is { IsReparsePoint: true })
            {
                var target = new FileInfo(link.FinalPath).ResolveLinkTarget(returnFinalTarget: true)!.FullName;
                return (Path.GetDirectoryName(target)!, Path.GetFileName(target));
            }
        }

        using var parent = OpenParentDirectory(path);
        var relativePath = RelativePath(path);
        return (parent.FinalPath, relativePath[(relativePath.LastIndexOf('/') + 1)..]);
    }

    /// <summary>
    /// 按物理路径打开并校验提交所在的目录：请求 FILE_TRAVERSE | FILE_READ_ATTRIBUTES 使共享方式生效，共享方式不含
    /// FILE_SHARE_DELETE。重命名以这个句柄为 RootDirectory 打开目标目录时请求 FILE_WRITE_DATA 与
    /// FILE_SHARE_READ | FILE_SHARE_WRITE，与这个句柄不冲突（FILE_RENAME_INFORMATION 文档推荐的组合）。
    /// Opens and verifies the commit directory by its physical path: FILE_TRAVERSE | FILE_READ_ATTRIBUTES is requested
    /// so the share mode, which lacks FILE_SHARE_DELETE, applies. A rename that opens the target directory with this
    /// handle as RootDirectory requests FILE_WRITE_DATA with FILE_SHARE_READ | FILE_SHARE_WRITE, which does not
    /// conflict with it (the combination recommended by the FILE_RENAME_INFORMATION documentation).
    /// </summary>
    private VerifiedHandle OpenCommitDirectory(string directoryPath, string path)
    {
        var directory = Open(
            directoryPath,
            path,
            WindowsNative.FILE_TRAVERSE | WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.ShareReadWrite,
            WindowsNative.OPEN_EXISTING,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS,
            out var error
        );
        if (directory is { IsDirectory: true })
        {
            return directory;
        }

        directory?.Dispose();
        throw error is 0 or WindowsNative.ERROR_FILE_NOT_FOUND or WindowsNative.ERROR_PATH_NOT_FOUND
            ? new AgwException(ErrorCodes.DirectoryNotFound, $"The parent directory of '{path}' was not found.")
            : WindowsNative.CreateException(error, $"Cannot open the directory of '{path}'");
    }

    /// <summary>
    /// 通过校验过的句柄打开提交目录中的 <paramref name="name"/>，即提交时被替换的文件；不存在时返回 null。以
    /// FILE_FLAG_OPEN_REPARSE_POINT 打开、不跟随重解析点：这个条目在定位之后被换成链接时，读取的会是链接目标而重命名替换的是
    /// 链接条目，因此按文件已变化处理，抛出 <c>FileChanged</c>，与 Unix 上的 O_NOFOLLOW 一致。文件按路径打开，路径中的上级
    /// 目录可能已被替换，因此还对比两个句柄的最终路径：打开的文件的父目录不是提交目录时同样抛出 <c>FileChanged</c>。
    /// Opens <paramref name="name"/> in the commit directory, the file the commit replaces, through a verified handle;
    /// returns null when it does not exist. It is opened with FILE_FLAG_OPEN_REPARSE_POINT without following reparse
    /// points: if the entry was swapped for a link after it was located, reading would see the link target while the
    /// rename replaces the link entry, so this is treated as a change and <c>FileChanged</c> is thrown, matching
    /// O_NOFOLLOW on Unix. The file is opened by path and an ancestor directory in that path may have been replaced, so
    /// the final paths of both handles are compared as well: <c>FileChanged</c> is also thrown when the opened file's
    /// parent is not the commit directory.
    /// </summary>
    private VerifiedHandle? OpenCommitTarget(VerifiedHandle parent, string name, uint access, string path)
    {
        var opened = Open(
            Path.Combine(parent.FinalPath, name),
            path,
            access,
            WindowsNative.OPEN_EXISTING,
            WindowsNative.FILE_FLAG_OPEN_REPARSE_POINT,
            out var error
        );
        if (opened != null)
        {
            if (!opened.IsReparsePoint && IsInDirectory(opened, parent))
            {
                return opened;
            }

            opened.Dispose();
            throw AtomicFileWrite.FileChanged(path);
        }

        return error is WindowsNative.ERROR_FILE_NOT_FOUND or WindowsNative.ERROR_PATH_NOT_FOUND
            ? null
            : throw WindowsNative.CreateException(error, $"Cannot open '{path}'");
    }

    /// <summary>
    /// 判断 <paramref name="file"/> 是否直接位于 <paramref name="directory"/> 中：对比文件的最终路径的父目录与目录句柄此刻的
    /// 最终路径，两者都通过已校验的句柄读取。
    /// Decides whether <paramref name="file"/> lies directly in <paramref name="directory"/>: the parent of the file's
    /// final path is compared with the directory handle's current final path, both read through verified handles.
    /// </summary>
    private static bool IsInDirectory(VerifiedHandle file, VerifiedHandle directory) =>
        string.Equals(
            Path.GetDirectoryName(file.FinalPath),
            Path.TrimEndingDirectorySeparator(WindowsNative.GetFinalPath(directory.Handle)),
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>
    /// 通过校验过的句柄删除这个文件以前留下的临时文件；此时持有该文件的锁，这些临时文件都不再使用。
    /// Deletes earlier temporary files of this file through verified handles; the file's lock is held now, so none of
    /// them is in use.
    /// </summary>
    private void DeleteStaleTemporaryFiles(VerifiedHandle parent, string name)
    {
        foreach (var stale in AtomicFileWrite.FindTemporaryFiles(parent.FinalPath, name))
        {
            using var opened = Open(
                Path.Combine(parent.FinalPath, stale),
                stale,
                WindowsNative.DELETE | WindowsNative.FILE_READ_ATTRIBUTES,
                WindowsNative.OPEN_EXISTING,
                WindowsNative.FILE_FLAG_OPEN_REPARSE_POINT,
                out var error
            );
            if (opened != null)
            {
                WindowsNative.MarkForDeletion(opened.Handle, $"Cannot delete temporary file '{stale}'");
            }
            else if (error is not (WindowsNative.ERROR_FILE_NOT_FOUND or WindowsNative.ERROR_PATH_NOT_FOUND))
            {
                throw WindowsNative.CreateException(error, $"Cannot open temporary file '{stale}'");
            }
        }
    }

    /// <summary>
    /// 原子提交：创建临时文件，写入并刷新到磁盘，复核后按句柄重命名；提交前失败或取消时通过句柄删除临时文件。复制使用
    /// <see cref="FilePathLease.Token"/>，调用方取消或租约丢失时中止。
    /// The atomic commit: creates the temporary file, writes and flushes it to disk, and renames it by handle after the
    /// re-check; the temporary file is deleted through its handle when anything fails or is canceled before the commit.
    /// Copying uses <see cref="FilePathLease.Token"/> and aborts when the caller cancels or a lease is lost.
    /// </summary>
    private async Task CommitWriteAsync(
        VerifiedHandle parent,
        string name,
        Stream content,
        bool overwrite,
        FileVersion? expected,
        FilePathLease pathLock,
        string path
    )
    {
        var temporaryName = AtomicFileWrite.CreateTemporaryName(name);
        VerifiedHandle? temporary = null;
        var committed = false;
        try
        {
            temporary = CreateTemporaryFile(parent, temporaryName, path);
            await AtomicFileWrite.CopyAsync(content, temporary.Handle, pathLock.Token).ConfigureAwait(false);
            Commit(parent, temporary, name, overwrite, expected, pathLock, path);
            committed = true;
        }
        finally
        {
            if (temporary != null)
            {
                if (!committed)
                {
                    AtomicFileWrite.DeleteTemporaryFile(
                        () =>
                            WindowsNative.MarkForDeletion(
                                temporary.Handle,
                                $"Cannot delete temporary file '{temporaryName}'"
                            ),
                        path,
                        temporaryName,
                        _logger
                    );
                }

                temporary.Dispose();
            }
        }
    }

    /// <summary>
    /// 在提交目录中以 CREATE_NEW 创建临时文件并校验它的最终路径，同时校验它的父目录就是提交目录。
    /// Creates the temporary file in the commit directory with CREATE_NEW and verifies its final path, including that
    /// its parent is the commit directory.
    /// </summary>
    private VerifiedHandle CreateTemporaryFile(VerifiedHandle parent, string temporaryName, string path)
    {
        var temporary =
            Open(
                Path.Combine(parent.FinalPath, temporaryName),
                path,
                WindowsNative.GENERIC_READ | WindowsNative.GENERIC_WRITE | WindowsNative.DELETE,
                WindowsNative.CREATE_NEW,
                0,
                out var error
            ) ?? throw WindowsNative.CreateException(error, $"Cannot create a temporary file for '{path}'");
        if (
            string.Equals(
                Path.GetDirectoryName(temporary.FinalPath),
                Path.TrimEndingDirectorySeparator(parent.FinalPath),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return temporary;
        }

        using (temporary)
        {
            WindowsNative.MarkForDeletion(
                temporary.Handle,
                $"Cannot remove temporary file '{temporaryName}' created outside its directory"
            );
        }

        throw new AgwException(
            ErrorCodes.FilePathOutsideRoot,
            $"Path '{path}' resolves outside the Project directories."
        );
    }

    /// <summary>
    /// 复核并提交：调用方已取消或租约已丢失时抛出 <see cref="OperationCanceledException"/>；有写入前提时重新打开目标并对比
    /// 文件标识、大小与修改时间；随后对临时文件句柄调用 NtSetInformationFile(FileRenameInformation)，RootDirectory 为持有的
    /// 目录句柄，不按路径重新解析。复核与重命名之间没有 await；不替换时目标已存在由重命名本身失败。目标是只读文件时重命名以
    /// ERROR_ACCESS_DENIED 失败，只读文件不被替换，抛出 <c>FileNotWritable</c>。
    /// Re-checks and commits: throws <see cref="OperationCanceledException"/> when the caller has canceled or the lease
    /// is lost; with a write precondition the target is reopened and its identity, size and modification time are
    /// compared; then NtSetInformationFile(FileRenameInformation) runs on the temporary file handle with the held
    /// directory handle as RootDirectory, resolving no path again. There is no await between the re-check and the
    /// rename, and without overwrite an existing target makes the rename itself fail. A read-only target makes the
    /// rename fail with ERROR_ACCESS_DENIED, so a read-only file is never replaced and <c>FileNotWritable</c> is
    /// thrown.
    /// </summary>
    private void Commit(
        VerifiedHandle parent,
        VerifiedHandle temporary,
        string name,
        bool overwrite,
        FileVersion? expected,
        FilePathLease pathLock,
        string path
    )
    {
        pathLock.ThrowIfLost();
        if (expected is { } version)
        {
            using var current =
                OpenCommitTarget(parent, name, WindowsNative.FILE_READ_ATTRIBUTES, path)
                ?? throw AtomicFileWrite.FileChanged(path);
            AtomicFileWrite.VerifyVersion(version, WindowsNative.GetFileVersion(current.Handle), path);
        }

        var error = WindowsNative.Rename(temporary.Handle, parent.Handle, name, replace: overwrite);
        if (error == 0)
        {
            return;
        }

        if (error == WindowsNative.ERROR_ACCESS_DENIED)
        {
            throw AtomicFileWrite.NotWritable(path);
        }

        throw !overwrite && error is WindowsNative.ERROR_ALREADY_EXISTS or WindowsNative.ERROR_FILE_EXISTS
            ? AtomicFileWrite.AlreadyExists(path)
            : WindowsNative.CreateException(error, $"Cannot replace '{path}'");
    }

    private void Delete(string path, bool recursive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 带 OPEN_REPARSE_POINT 打开，链接与 junction 删除的是其本身；目录在递归时先清空，非递归时只标记目录本身，目录
        // 非空则标记失败，期间新建的文件不会被删除。
        // Opened with OPEN_REPARSE_POINT so a link or junction is deleted itself; a directory is emptied first when
        // recursive, and otherwise only the directory itself is marked, which fails when it has entries, so files created
        // meanwhile are never deleted.
        using var opened = OpenExisting(
            path,
            WindowsNative.DELETE | WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS | WindowsNative.FILE_FLAG_OPEN_REPARSE_POINT
        );
        if (opened == null)
        {
            return;
        }

        if (recursive && opened.IsDirectory && !opened.IsReparsePoint)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(opened.FinalPath))
            {
                Delete(Combine(path, Path.GetFileName(child)), recursive: true, ct);
            }
        }

        WindowsNative.MarkForDeletion(opened.Handle, $"Cannot delete '{path}'");
    }

    /// <summary>
    /// 打开并校验 <paramref name="path"/> 的父目录（本实例内的相对路径），不存在或不是目录时抛出 <c>DirectoryNotFound</c>。
    /// Opens and verifies the parent directory of <paramref name="path"/> (relative to this instance), throwing
    /// <c>DirectoryNotFound</c> when it is missing or not a directory.
    /// </summary>
    private VerifiedHandle OpenParentDirectory(string path)
    {
        var relativePath = RelativePath(path);
        var separator = relativePath.LastIndexOf('/');
        var parent = OpenExisting(
            separator < 0 ? string.Empty : relativePath[..separator],
            WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS
        );
        if (parent is { IsDirectory: true })
        {
            return parent;
        }

        parent?.Dispose();
        throw new AgwException(ErrorCodes.DirectoryNotFound, $"The parent directory of '{path}' was not found.");
    }

    /// <summary>
    /// 从 Project 根目录开始逐段确保目录存在（含子文件系统自身的目录）：每一段先按句柄校验，缺失时在已校验父目录的物理路径下
    /// 创建，再重新按句柄校验。<paramref name="rootRelativeDirectory"/> 相对 Project 根目录。
    /// Ensures the directories segment by segment starting from the Project root (including the sub file system's own
    /// directory): each segment is verified through a handle first, created under the physical path of the verified
    /// parent when missing, and verified through a handle again afterwards. <paramref name="rootRelativeDirectory"/>
    /// is relative to the Project root.
    /// </summary>
    private void EnsureDirectories(string rootRelativeDirectory)
    {
        var current = string.Empty;
        foreach (var segment in rootRelativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Combine(current, segment);
            using var existing = OpenRootRelative(next);
            if (existing != null)
            {
                if (!existing.IsDirectory)
                {
                    throw new AgwException(ErrorCodes.FileOperationFailed, $"'{next}' exists and is not a directory.");
                }

                current = next;
                continue;
            }

            using (var parent = OpenRootRelative(current))
            {
                if (parent is not { IsDirectory: true })
                {
                    throw new AgwException(
                        ErrorCodes.DirectoryNotFound,
                        $"The parent directory of '{next}' was not found."
                    );
                }

                Directory.CreateDirectory(Path.Combine(parent.FinalPath, segment));
            }

            using var created = OpenRootRelative(next);
            if (created is not { IsDirectory: true })
            {
                throw new AgwException(ErrorCodes.DirectoryNotFound, $"Cannot create directory '{next}'.");
            }

            current = next;
        }
    }

    private void EnsureParentDirectories(string path)
    {
        var rootRelative = RootRelativePath(path);
        var separator = rootRelative.LastIndexOf('/');
        if (separator > 0)
        {
            EnsureDirectories(rootRelative[..separator]);
        }
    }

    /// <summary>
    /// 以 Project 根目录为基准打开目录或文件并校验句柄；不存在时返回 null。
    /// Opens a directory or file relative to the Project root and verifies the handle; returns null when it is missing.
    /// </summary>
    private VerifiedHandle? OpenRootRelative(string rootRelativePath)
    {
        var opened = Open(
            _root.ResolvePhysicalPath(rootRelativePath),
            rootRelativePath,
            WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.OPEN_EXISTING,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS,
            out var error
        );
        if (opened != null)
        {
            return opened;
        }

        return error is WindowsNative.ERROR_FILE_NOT_FOUND or WindowsNative.ERROR_PATH_NOT_FOUND
            ? null
            : throw WindowsNative.CreateException(error, $"Cannot open '{rootRelativePath}'");
    }

    /// <summary>
    /// 把本实例的相对路径转换为相对 Project 根目录的路径（词法校验后带上子文件系统前缀）。
    /// Converts a path relative to this instance into one relative to the Project root (the sub file system prefix is
    /// prepended after lexical validation).
    /// </summary>
    private string RootRelativePath(string path) => Combine(_prefix, RelativePath(path));

    /// <summary>
    /// 打开已有的普通文件；不存在或是目录时抛出 <c>FileNotFound</c>。带 FILE_FLAG_BACKUP_SEMANTICS 打开，目录也能得到句柄，
    /// 再按句柄判断是否为目录。
    /// Opens an existing regular file; throws <c>FileNotFound</c> when it is missing or is a directory. It is opened
    /// with FILE_FLAG_BACKUP_SEMANTICS so a directory also yields a handle, which is then checked for being a
    /// directory.
    /// </summary>
    private VerifiedHandle OpenExistingFile(string path, uint access)
    {
        ResolveFilePath(path);
        var opened = OpenExisting(path, access, WindowsNative.FILE_FLAG_BACKUP_SEMANTICS);
        if (opened == null || opened.IsDirectory)
        {
            opened?.Dispose();
            throw new AgwException(ErrorCodes.FileNotFound, $"File '{path}' was not found.");
        }

        return opened;
    }

    /// <summary>
    /// 打开已有文件；不存在时用 CREATE_NEW 创建并校验句柄，句柄落在允许范围之外时通过句柄标记删除后拒绝。
    /// <paramref name="mustCreate"/> 为 true 且文件已存在时返回 null。
    /// Opens an existing file; when it is missing, creates it with CREATE_NEW and verifies the handle, marking it for
    /// deletion through the handle and rejecting when it lands outside the allowed directories. Returns null when
    /// <paramref name="mustCreate"/> is true and the file already exists.
    /// </summary>
    private VerifiedHandle? OpenOrCreateFile(string path, bool mustCreate)
    {
        while (true)
        {
            var existing = OpenExisting(path, WindowsNative.GENERIC_READ | WindowsNative.GENERIC_WRITE, 0);
            if (existing != null)
            {
                if (existing.IsDirectory)
                {
                    existing.Dispose();
                    throw new AgwException(ErrorCodes.InvalidParam, $"'{path}' is not a regular file.");
                }

                if (!mustCreate)
                {
                    return existing;
                }

                existing.Dispose();
                return null;
            }

            var created = Open(
                ResolveFilePath(path),
                path,
                WindowsNative.GENERIC_READ | WindowsNative.GENERIC_WRITE | WindowsNative.DELETE,
                WindowsNative.CREATE_NEW,
                0,
                out var error
            );
            if (created != null)
            {
                return created;
            }

            if (error is WindowsNative.ERROR_FILE_EXISTS or WindowsNative.ERROR_ALREADY_EXISTS)
            {
                if (mustCreate)
                {
                    return null;
                }

                continue;
            }

            throw WindowsNative.CreateException(error, $"Cannot create file '{path}'");
        }
    }

    /// <summary>
    /// 打开已有对象并校验句柄；不存在时返回 null。
    /// Opens an existing object and verifies the handle; returns null when it does not exist.
    /// </summary>
    private VerifiedHandle? OpenExisting(string path, uint access, uint flags)
    {
        var opened = Open(ResolveLexicalPath(path), path, access, WindowsNative.OPEN_EXISTING, flags, out var error);
        if (opened != null)
        {
            return opened;
        }

        return error is WindowsNative.ERROR_FILE_NOT_FOUND or WindowsNative.ERROR_PATH_NOT_FOUND
            ? null
            : throw WindowsNative.CreateException(error, $"Cannot open '{path}'");
    }

    private VerifiedHandle OpenVerifiedAbsolute(string fullPath, uint access) =>
        Open(fullPath, fullPath, access, WindowsNative.OPEN_EXISTING, 0, out var error)
        ?? throw WindowsNative.CreateException(error, $"Cannot open '{fullPath}'");

    /// <summary>
    /// 按路径打开句柄并读取其最终物理路径，路径不在允许范围内时拒绝。新建的对象在拒绝前通过句柄标记删除。
    /// Opens a handle by path and reads its final physical path, rejecting it when the path is not inside the allowed
    /// directories. A newly created object is marked for deletion through the handle before the rejection.
    /// </summary>
    private VerifiedHandle? Open(
        string fullPath,
        string path,
        uint access,
        uint disposition,
        uint flags,
        out int error
    ) => Open(fullPath, path, access, WindowsNative.ShareAll, disposition, flags, out error);

    /// <summary>
    /// 与上一个重载相同，共享方式由 <paramref name="share"/> 指定。
    /// The same as the previous overload, with the share mode given by <paramref name="share"/>.
    /// </summary>
    private VerifiedHandle? Open(
        string fullPath,
        string path,
        uint access,
        uint share,
        uint disposition,
        uint flags,
        out int error
    )
    {
        var handle = WindowsNative.CreateFile(fullPath, access, share, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            error = WindowsNative.LastError;
            handle.Dispose();
            if (error is WindowsNative.ERROR_FILE_NOT_FOUND or WindowsNative.ERROR_PATH_NOT_FOUND)
            {
                EnsureNearestExistingAncestorInside(fullPath, path);
            }

            return null;
        }

        error = 0;
        var finalPath = WindowsNative.GetFinalPath(handle);
        if (IsInside(finalPath))
        {
            return new VerifiedHandle(handle, finalPath, File.GetAttributes(handle));
        }

        using (handle)
        {
            if (disposition == WindowsNative.CREATE_NEW)
            {
                WindowsNative.MarkForDeletion(
                    handle,
                    $"Cannot remove '{path}' created outside the Project directories"
                );
            }
        }

        throw new AgwException(
            ErrorCodes.FilePathOutsideRoot,
            $"Path '{path}' resolves outside the Project directories."
        );
    }

    /// <summary>
    /// 目标不存在时，沿词法路径向上找到最近的已存在祖先目录并校验其句柄的物理位置。这样即使最终文件不存在，路径经过指向外部的
    /// 目录链接时仍然报告越界，而不是"不存在"，与 Unix 实现一致，也不会暴露外部文件是否存在。
    /// When the target is missing, walks up the lexical path to the nearest existing ancestor directory and verifies
    /// the physical location of its handle. A path passing through a directory link that points outside is then
    /// reported as outside instead of "missing", matching the Unix implementation and never revealing whether an
    /// outside file exists.
    /// </summary>
    private void EnsureNearestExistingAncestorInside(string fullPath, string path)
    {
        var rootFullPath = Path.TrimEndingDirectorySeparator(_root.ResolvePhysicalPath(string.Empty));
        var current = Path.GetDirectoryName(fullPath);
        while (current != null)
        {
            using var handle = WindowsNative.CreateFile(
                current,
                WindowsNative.FILE_READ_ATTRIBUTES,
                WindowsNative.ShareAll,
                IntPtr.Zero,
                WindowsNative.OPEN_EXISTING,
                WindowsNative.FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero
            );
            if (!handle.IsInvalid)
            {
                if (IsInside(WindowsNative.GetFinalPath(handle)))
                {
                    return;
                }

                throw new AgwException(
                    ErrorCodes.FilePathOutsideRoot,
                    $"Path '{path}' resolves outside the Project directories."
                );
            }

            var error = WindowsNative.LastError;
            if (error is not (WindowsNative.ERROR_FILE_NOT_FOUND or WindowsNative.ERROR_PATH_NOT_FOUND))
            {
                throw WindowsNative.CreateException(error, $"Cannot open '{path}'");
            }

            if (
                string.Equals(
                    Path.TrimEndingDirectorySeparator(current),
                    rootFullPath,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw new AgwException(
                    ErrorCodes.ResourceNotFound,
                    $"Project directory is unavailable: '{rootFullPath}'."
                );
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private bool IsInside(string finalPath)
    {
        foreach (var root in _allowedRoots)
        {
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (
                finalPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                || finalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    private string ResolveLexicalPath(string path)
    {
        RejectNul(path);
        return _inner.ResolvePhysicalPath(path);
    }

    private string ResolveFilePath(string path)
    {
        var fullPath = ResolveLexicalPath(path);
        if (RelativePath(path).Length == 0)
        {
            throw new AgwException(ErrorCodes.FilePathRequired, "A file path must not be empty.");
        }

        return fullPath;
    }

    private string RelativePath(string path) => _inner.GetRelativePath(ResolveLexicalPath(path));

    private static void RejectNul(string path)
    {
        if (path.Contains('\0'))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Path must not contain NUL characters.");
        }
    }

    private static string Combine(string directory, string name) =>
        directory.Length == 0 ? name
        : name.Length == 0 ? directory
        : directory + "/" + name;

    private static string ToRelativePath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// 已校验的句柄：句柄本身、最终物理路径与属性。
    /// A verified handle: the handle itself, its final physical path and its attributes.
    /// </summary>
    private sealed class VerifiedHandle : IDisposable
    {
        public VerifiedHandle(SafeFileHandle handle, string finalPath, FileAttributes attributes)
        {
            Handle = handle;
            FinalPath = finalPath;
            Attributes = attributes;
        }

        public SafeFileHandle Handle { get; }
        public string FinalPath { get; }
        public FileAttributes Attributes { get; }
        public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
        public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

        public void Dispose() => Handle.Dispose();
    }
}
