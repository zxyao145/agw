using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Shared.Exceptions;
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

    /// <param name="root">Project 根目录的词法文件系统。The lexical file system of the Project root.</param>
    /// <param name="prefix">
    /// 本实例相对 Project 根目录的子目录（子文件系统），空字符串表示根目录本身；目录创建从根目录开始，因此子目录本身也会被创建。
    /// The subdirectory of this instance relative to the Project root (a sub file system), empty for the root itself;
    /// directory creation starts from the root, so the subdirectory itself is created as well.
    /// </param>
    /// <param name="allowedRoots">允许的目录的最终物理路径。The final physical paths of the allowed directories.</param>
    private WindowsConfinedFileSystem(LocalFileSystem root, string prefix, string[] allowedRoots)
    {
        _root = root;
        _prefix = prefix;
        _inner = prefix.Length == 0 ? root : (LocalFileSystem)root.GetSubFileSystem(prefix);
        _allowedRoots = allowedRoots;
    }

    public static WindowsConfinedFileSystem Create(string rootFullPath, IReadOnlyList<string> allowedRoots)
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

        return new WindowsConfinedFileSystem(new LocalFileSystem(rootFullPath), string.Empty, roots.ToArray());
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
        using var pathLock = await LocalFilePathLocks.AcquireAsync(fullPath, ct).ConfigureAwait(false);
        EnsureParentDirectories(path);
        using var opened = OpenOrCreateFile(path, mustCreate: false)!;
        await HandleTextIO.WriteAllTextAsync(opened.Handle, content, ct).ConfigureAwait(false);
    }

    public async Task<bool> CreateTextFileAsync(string path, string content, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        using var pathLock = await LocalFilePathLocks.AcquireAsync(fullPath, ct).ConfigureAwait(false);
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
        using var pathLock = await LocalFilePathLocks.AcquireAsync(fullPath, ct).ConfigureAwait(false);
        using var opened = OpenExistingFile(path, WindowsNative.GENERIC_READ | WindowsNative.GENERIC_WRITE);
        var content = await HandleTextIO.ReadAllTextAsync(opened.Handle, ct).ConfigureAwait(false);
        var (newContent, count) = TextContentEditor.ApplyReplace(content, oldString, newString, replaceAll);
        await HandleTextIO.WriteAllTextAsync(opened.Handle, newContent, ct).ConfigureAwait(false);
        return count;
    }

    public async Task ReplaceLinesAsync(string path, IReadOnlyList<AgwFileLineEdit> edits, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        using var pathLock = await LocalFilePathLocks.AcquireAsync(fullPath, ct).ConfigureAwait(false);
        using var opened = OpenExistingFile(path, WindowsNative.GENERIC_READ | WindowsNative.GENERIC_WRITE);
        var content = await HandleTextIO.ReadAllTextAsync(opened.Handle, ct).ConfigureAwait(false);
        var newContent = TextContentEditor.ApplyReplaceLines(content, edits);
        await HandleTextIO.WriteAllTextAsync(opened.Handle, newContent, ct).ConfigureAwait(false);
    }

    public IAgwFileSystem GetSubFileSystem(string path) =>
        new WindowsConfinedFileSystem(_root, RootRelativePath(path), _allowedRoots);

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        EnsureDirectories(RootRelativePath(path));
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        using var pathLock = await LocalFilePathLocks.AcquireAsync(fullPath, ct).ConfigureAwait(false);
        Delete(path, ct);
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

    private void Delete(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 带 OPEN_REPARSE_POINT 打开，链接与 junction 删除的是其本身；目录先递归清空再通过句柄标记删除。
        // Opened with OPEN_REPARSE_POINT so a link or junction is deleted itself; a directory is emptied recursively and
        // then marked for deletion through its handle.
        using var opened = OpenExisting(
            path,
            WindowsNative.DELETE | WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.FILE_FLAG_BACKUP_SEMANTICS | WindowsNative.FILE_FLAG_OPEN_REPARSE_POINT
        );
        if (opened == null)
        {
            return;
        }

        if (opened.IsDirectory && !opened.IsReparsePoint)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(opened.FinalPath))
            {
                Delete(Combine(path, Path.GetFileName(child)), ct);
            }
        }

        WindowsNative.MarkForDeletion(opened.Handle, $"Cannot delete '{path}'");
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

    private VerifiedHandle OpenExistingFile(string path, uint access)
    {
        ResolveFilePath(path);
        var opened = OpenExisting(path, access, 0);
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
    private VerifiedHandle? Open(string fullPath, string path, uint access, uint disposition, uint flags, out int error)
    {
        var handle = WindowsNative.CreateFile(
            fullPath,
            access,
            WindowsNative.ShareAll,
            IntPtr.Zero,
            disposition,
            flags,
            IntPtr.Zero
        );
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
