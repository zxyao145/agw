using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Agw.Files.Infrastructure.Storage.Confined;

/// <summary>
/// 基于目录句柄的受限文件系统。每个操作都从根目录句柄出发，用不跟随符号链接的 openat 逐段打开路径，遇到符号链接时读取目标并
/// 继续以句柄方式解析；只有当前目录句柄的身份（设备号与 inode）落在允许的根目录内时才执行最终操作，之后的 IO 全部使用已打开
/// 的句柄，因此校验与访问之间不存在可被替换的路径。
/// A confined file system built on directory handles. Every operation starts from the root handle and opens the path
/// segment by segment with openat without following symbolic links; a link is read and resolved through handles as
/// well. The final operation runs only when the identity (device and inode) of the current directory handle lies inside
/// an allowed root, and all IO afterwards uses the opened handles, so no path can be swapped between validation and
/// access.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixConfinedFileSystem : IAgwFileSystem
{
    private const int MaxSymbolicLinkHops = 40;
    private const uint DirectoryMode = 0x1FF;
    private const uint FileMode = 0x1B6;

    private readonly string _rootPath;
    private readonly string[] _prefix;
    private readonly FileIdentity[] _allowedRoots;
    private readonly FilePathLocks _pathLocks;
    private readonly ILogger _logger;

    internal UnixConfinedFileSystem(
        string rootPath,
        string[] prefix,
        FileIdentity[] allowedRoots,
        FilePathLocks pathLocks,
        ILogger logger
    )
    {
        _rootPath = rootPath;
        _prefix = prefix;
        _allowedRoots = allowedRoots;
        _pathLocks = pathLocks;
        _logger = logger;
    }

    /// <summary>
    /// 打开每个允许的根目录并记录其身份；不存在的目录无法被进入，直接略过。
    /// Opens every allowed root and records its identity; a missing directory cannot be entered and is skipped.
    /// </summary>
    internal static FileIdentity[] ResolveAllowedRoots(IReadOnlyList<string> allowedRoots) =>
        allowedRoots.Select(DirectoryIdentities.TryRead).OfType<FileIdentity>().ToArray();

    public Task<bool> ExistsFileAsync(string path, CancellationToken ct)
    {
        using var walk = Locate(path, createDirectories: false);
        using var opened = OpenFinal(walk, UnixNative.O_RDONLY | UnixNative.O_NONBLOCK, path);
        return Task.FromResult(opened.Handle != null && opened.Status.IsRegularFile);
    }

    public Task<bool> ExistsDirectoryAsync(string path, CancellationToken ct)
    {
        using var walk = Locate(path, createDirectories: false);
        using var opened = OpenFinal(walk, UnixNative.O_RDONLY | UnixNative.O_DIRECTORY, path);
        return Task.FromResult(opened.Handle != null);
    }

    public Task<FileEntry?> StatAsync(string path, CancellationToken ct)
    {
        using var walk = Locate(path, createDirectories: false);
        using var opened = OpenFinal(walk, UnixNative.O_RDONLY | UnixNative.O_NONBLOCK, path);
        if (opened.Handle == null)
        {
            return Task.FromResult<FileEntry?>(null);
        }

        var status = opened.Status;
        if (status.IsDirectory)
        {
            return Task.FromResult<FileEntry?>(
                new FileEntry(walk.RelativePath, IsDirectory: true, Size: 0, status.LastModifiedUtc)
            );
        }

        return Task.FromResult<FileEntry?>(
            status.IsRegularFile
                ? new FileEntry(walk.RelativePath, IsDirectory: false, status.Size, status.LastModifiedUtc)
                : null
        );
    }

    public async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
    {
        using var walk = Locate(path, createDirectories: false);
        using var handle = OpenExistingFile(walk, UnixNative.O_RDONLY, path);
        return await HandleTextIO.ReadAllTextAsync(handle, ct).ConfigureAwait(false);
    }

    public async Task<string[]> ReadAllLinesAsync(string path, CancellationToken ct)
    {
        using var walk = Locate(path, createDirectories: false);
        using var handle = OpenExistingFile(walk, UnixNative.O_RDONLY, path);
        var content = await HandleTextIO.ReadAllTextAsync(handle, ct).ConfigureAwait(false);
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
        using var walk = Locate(path, createDirectories: true);
        RequireFileName(walk);
        await using var pathLock = await _pathLocks.AcquireContentAsync(walk.FullPath, ct).ConfigureAwait(false);
        using var handle = OpenOrCreateFile(walk, path, mustCreate: false)!;
        await HandleTextIO.WriteAllTextAsync(handle, content, ct).ConfigureAwait(false);
    }

    public async Task<bool> CreateTextFileAsync(string path, string content, CancellationToken ct)
    {
        using var walk = Locate(path, createDirectories: true);
        RequireFileName(walk);
        await using var pathLock = await _pathLocks.AcquireContentAsync(walk.FullPath, ct).ConfigureAwait(false);
        using var handle = OpenOrCreateFile(walk, path, mustCreate: true);
        if (handle == null)
        {
            return false;
        }

        await HandleTextIO.WriteAllTextAsync(handle, content, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<string>> ReadLinesAsync(
        string path,
        int startLine,
        int? endLine,
        CancellationToken ct
    )
    {
        using var walk = Locate(path, createDirectories: false);
        using var handle = OpenExistingFile(walk, UnixNative.O_RDONLY, path);
        var content = await HandleTextIO.ReadAllTextAsync(handle, ct).ConfigureAwait(false);
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
        using var walk = Locate(path, createDirectories: false);
        RequireFileName(walk);
        await using var pathLock = await _pathLocks.AcquireContentAsync(walk.FullPath, ct).ConfigureAwait(false);
        using var handle = OpenExistingFile(walk, UnixNative.O_RDWR, path);
        var content = await HandleTextIO.ReadAllTextAsync(handle, ct).ConfigureAwait(false);
        var (newContent, count) = TextContentEditor.ApplyReplace(content, oldString, newString, replaceAll);
        await HandleTextIO.WriteAllTextAsync(handle, newContent, ct).ConfigureAwait(false);
        return count;
    }

    public async Task ReplaceLinesAsync(string path, IReadOnlyList<AgwFileLineEdit> edits, CancellationToken ct)
    {
        using var walk = Locate(path, createDirectories: false);
        RequireFileName(walk);
        await using var pathLock = await _pathLocks.AcquireContentAsync(walk.FullPath, ct).ConfigureAwait(false);
        using var handle = OpenExistingFile(walk, UnixNative.O_RDWR, path);
        var content = await HandleTextIO.ReadAllTextAsync(handle, ct).ConfigureAwait(false);
        var newContent = TextContentEditor.ApplyReplaceLines(content, edits);
        await HandleTextIO.WriteAllTextAsync(handle, newContent, ct).ConfigureAwait(false);
    }

    public IAgwFileSystem GetSubFileSystem(string path) =>
        new UnixConfinedFileSystem(
            _rootPath,
            [.. _prefix, .. NormalizeSegments(path)],
            _allowedRoots,
            _pathLocks,
            _logger
        );

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        using var walk = Locate(path, createDirectories: true);
        await using var pathLock = await _pathLocks.AcquireEntryAsync(walk.FullPath, ct).ConfigureAwait(false);
        while (true)
        {
            RequireParent(walk, path);
            // 名称为空表示根目录本身（或链接最终指向根目录），它必然已经存在。
            // An empty name means the root itself (or a link that ends at the root), which necessarily exists.
            if (walk.FinalName == null)
            {
                return;
            }

            if (UnixNative.MkDirAt(UnixNative.Fd(walk.Directory), walk.FinalName, DirectoryMode) == 0)
            {
                return;
            }

            var errno = UnixNative.LastError;
            if (errno != UnixNative.EEXIST)
            {
                throw UnixNative.CreateException(errno, $"Cannot create directory '{path}'");
            }

            // 已存在的条目必须是目录或指向目录的符号链接；OpenFinal 会沿链接继续解析。
            // The existing entry must be a directory or a symbolic link to one; OpenFinal follows the link.
            using var opened = OpenFinal(walk, UnixNative.O_RDONLY | UnixNative.O_DIRECTORY, path);
            if (opened.Handle != null)
            {
                return;
            }

            if (opened.Failure == OpenFailure.NotDirectory)
            {
                throw UnixNative.CreateException(UnixNative.EEXIST, $"Cannot create directory '{path}'");
            }
        }
    }

    public async Task DeleteAsync(string path, bool recursive, CancellationToken ct)
    {
        using var walk = Locate(path, createDirectories: false);
        var name = RequireFileName(walk);
        await using var pathLock = await _pathLocks.AcquireEntryAsync(walk.FullPath, ct).ConfigureAwait(false);
        if (!walk.Found)
        {
            return;
        }

        walk.RequireInside(path);
        var directory = UnixNative.Fd(walk.Directory);
        // unlinkat 不跟随符号链接：文件和链接本身直接删除；目录在递归时先清空再移除，非递归时只调用一次 AT_REMOVEDIR，
        // 目录非空则失败，期间新建的文件不会被删除。
        // unlinkat never follows symbolic links: files and links themselves are removed directly; a directory is
        // emptied first when recursive, and otherwise removed by a single AT_REMOVEDIR call that fails when the
        // directory has entries, so files created meanwhile are never deleted.
        if (UnixNative.UnlinkAt(directory, name, 0) == 0)
        {
            return;
        }

        var errno = UnixNative.LastError;
        if (errno == UnixNative.ENOENT)
        {
            return;
        }

        if (errno is not (UnixNative.EISDIR or UnixNative.EPERM))
        {
            throw UnixNative.CreateException(errno, $"Cannot delete '{path}'");
        }

        if (recursive)
        {
            using var child = OpenChildDirectory(walk.Directory, name);
            if (child == null)
            {
                return;
            }

            DeleteContents(child, ct);
        }

        if (UnixNative.UnlinkAt(directory, name, UnixNative.AT_REMOVEDIR) == 0)
        {
            return;
        }

        errno = UnixNative.LastError;
        if (errno == UnixNative.ENOENT)
        {
            return;
        }

        throw errno == UnixNative.ENOTEMPTY
            ? new AgwException(ErrorCodes.DirectoryNotEmpty, $"Directory '{path}' is not empty.")
            : UnixNative.CreateException(errno, $"Cannot delete '{path}'");
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        using var walk = Locate(path, createDirectories: false);
        var handle = OpenExistingFile(walk, UnixNative.O_RDONLY, path);
        try
        {
            return Task.FromResult<Stream>(new FileStream(handle, FileAccess.Read));
        }
        catch
        {
            handle.Dispose();
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
        using var walk = Locate(path, createDirectories: createParents);
        RequireFileName(walk);
        await using var pathLock = await _pathLocks.AcquireContentAsync(walk.FullPath, ct).ConfigureAwait(false);
        using var existing = OpenWriteTarget(walk, overwrite, path);
        RequireParent(walk, path);
        FileVersion? expected = null;
        if (expectedSha256 != null)
        {
            var handle = existing.Handle ?? throw AtomicFileWrite.FileChanged(path);
            expected = existing.Status.Version;
            var actualSha256 = await AtomicFileWrite.HashAsync(handle, ct).ConfigureAwait(false);
            AtomicFileWrite.VerifyHash(expectedSha256, actualSha256, path);
        }

        var name = RequireFileName(walk);
        DeleteStaleTemporaryFiles(walk.Directory, name);
        var permissions = existing.Handle != null ? existing.Status.Permissions : (UnixFileMode?)null;
        await UnixAtomicCommit
            .WriteAsync(
                walk.Directory,
                name,
                content,
                permissions,
                overwrite,
                expected,
                pathLock,
                path,
                walk.FullPath,
                _logger
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 打开写入目标并执行现有检查：OpenFinal 沿允许范围内的符号链接解析，之后 walk 停在最终目标所在目录，临时文件与重命名都在
    /// 这个目录句柄下进行。目标是目录、不是普通文件，或 <paramref name="overwrite"/> 为 false 而目标已存在时抛出；目标不存在时
    /// 返回不含句柄的结果。
    /// Opens the write target and runs the existing checks: OpenFinal follows symbolic links inside the allowed
    /// directories, after which walk rests on the final target's directory, and the temporary file and rename both
    /// happen under that directory handle. Throws when the target is a directory, is not a regular file, or exists
    /// while <paramref name="overwrite"/> is false; returns a result without a handle when the target is missing.
    /// </summary>
    private static OpenedFile OpenWriteTarget(Walk walk, bool overwrite, string path)
    {
        var existing = OpenFinal(walk, UnixNative.O_RDONLY | UnixNative.O_NONBLOCK, path);
        if (existing.Handle == null)
        {
            return existing.Failure == OpenFailure.Missing
                ? existing
                : throw new AgwException(ErrorCodes.FileNotFound, $"File '{path}' was not found.");
        }

        var status = existing.Status;
        if (overwrite && status.IsRegularFile)
        {
            return existing;
        }

        existing.Dispose();
        var occupied = !overwrite && (status.IsRegularFile || status.IsDirectory);
        throw occupied
            ? AtomicFileWrite.AlreadyExists(path)
            : new AgwException(ErrorCodes.InvalidParam, $"'{path}' is not a regular file.");
    }

    /// <summary>
    /// 删除同一目录中这个文件以前留下的临时文件；此时持有该文件的锁，这些临时文件都不再使用。
    /// Deletes earlier temporary files of this file in the same directory; the file's lock is held now, so none of them
    /// is in use.
    /// </summary>
    private static void DeleteStaleTemporaryFiles(SafeFileHandle directory, string name)
    {
        foreach (var (entryName, type) in ReadEntries(directory))
        {
            if (type != UnixNative.DT_DIR && AtomicFileWrite.IsTemporaryNameOf(entryName, name))
            {
                UnixAtomicCommit.DeleteTemporary(directory, entryName);
            }
        }
    }

    public async Task MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken ct)
    {
        using var source = Locate(sourcePath, createDirectories: false);
        var sourceName = RequireFileName(source);
        using var destination = Locate(destinationPath, createDirectories: false);
        var destinationName = RequireFileName(destination);
        await using var pathLock = await _pathLocks
            .AcquireEntryAsync(source.FullPath, destination.FullPath, ct)
            .ConfigureAwait(false);
        if (!source.Found)
        {
            throw new AgwException(ErrorCodes.FileNotFound, $"'{sourcePath}' was not found.");
        }

        source.RequireInside(sourcePath);
        RequireParent(destination, destinationPath);
        // 已存在的目录永远不被替换；renameat 在两个已校验的父目录句柄之间操作，源与目标的符号链接本身被移动或替换，不会被跟随。
        // An existing directory is never replaced; renameat works between the two verified parent directory handles, and
        // symbolic links at either end are moved or replaced themselves instead of being followed.
        if (overwrite)
        {
            using var existingDirectory = OpenChildDirectory(destination.Directory, destinationName);
            if (existingDirectory != null)
            {
                throw new AgwException(ErrorCodes.FileAlreadyExists, $"'{destinationPath}' already exists.");
            }
        }

        if (
            UnixNative.RenameAt(
                UnixNative.Fd(source.Directory),
                sourceName,
                UnixNative.Fd(destination.Directory),
                destinationName,
                replace: overwrite
            ) == 0
        )
        {
            return;
        }

        var errno = UnixNative.LastError;
        if (errno == UnixNative.ENOENT)
        {
            throw new AgwException(ErrorCodes.FileNotFound, $"'{sourcePath}' was not found.");
        }

        // EISDIR、ENOTDIR、ENOTEMPTY 来自源与目标类型不同或目标是目录，都表示目标已存在且不能被替换。
        // EISDIR, ENOTDIR and ENOTEMPTY come from a source and destination of different types or a directory
        // destination, all meaning the destination exists and cannot be replaced.
        if (
            errno == UnixNative.EEXIST
            || errno == UnixNative.EISDIR
            || errno == UnixNative.ENOTDIR
            || errno == UnixNative.ENOTEMPTY
        )
        {
            throw new AgwException(ErrorCodes.FileAlreadyExists, $"'{destinationPath}' already exists.");
        }

        throw errno == UnixNative.EINVAL
            ? new AgwException(ErrorCodes.InvalidParam, $"'{sourcePath}' cannot be moved into itself.")
            : UnixNative.CreateException(errno, $"Cannot move '{sourcePath}' to '{destinationPath}'");
    }

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(
        string path,
        string searchPattern,
        bool recursive,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        using var walk = Locate(path, createDirectories: false);
        using var opened = OpenFinal(walk, UnixNative.O_RDONLY | UnixNative.O_DIRECTORY, path);
        if (opened.Handle == null)
        {
            yield break;
        }

        foreach (
            var entry in EnumerateEntries(opened.Handle, walk.RelativePath, recursive, excludedDirectoryNames: null, ct)
        )
        {
            using (entry)
            {
                if (!FileSystemName.MatchesSimpleExpression(searchPattern, entry.Name, ignoreCase: false))
                {
                    continue;
                }

                if (entry.Handle != null)
                {
                    var status = entry.Status;
                    yield return new FileEntry(
                        entry.Path,
                        status.IsDirectory,
                        status.IsDirectory ? 0 : status.Size,
                        status.LastModifiedUtc
                    );
                    continue;
                }

                // 非递归枚举时，符号链接按其在允许目录内的目标展示；目标在外部或不存在时不展示。
                // In a non-recursive listing a symbolic link is shown as its target inside the allowed directories, and
                // is hidden when the target is outside or missing.
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
    }

    public async IAsyncEnumerable<AgwFileSearchResult> SearchAsync(
        string rootPath,
        SearchOptions options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        using var walk = Locate(rootPath, createDirectories: false);
        using var opened = OpenFinal(walk, UnixNative.O_RDONLY | UnixNative.O_DIRECTORY, rootPath);
        if (opened.Handle == null)
        {
            yield break;
        }

        HashSet<string>? excludedDirectoryNames = options.ExcludedDirectoryNames is { Count: > 0 }
            ? new HashSet<string>(options.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase)
            : null;
        var searchRoot = walk.RelativePath;
        var candidates = EnumerateSearchCandidates(
            opened.Handle,
            searchRoot,
            options.Recursive,
            excludedDirectoryNames,
            ct
        );
        await foreach (var result in FileContentSearch.SearchAsync(candidates, options, ct).ConfigureAwait(false))
        {
            yield return result;
        }
    }

    private IEnumerable<SearchCandidate> EnumerateSearchCandidates(
        SafeFileHandle directory,
        string searchRoot,
        bool recursive,
        HashSet<string>? excludedDirectoryNames,
        CancellationToken ct
    )
    {
        var searchPrefix = searchRoot.Length == 0 ? 0 : searchRoot.Length + 1;
        foreach (var entry in EnumerateEntries(directory, searchRoot, recursive, excludedDirectoryNames, ct))
        {
            // 句柄在候选被真正打开时移交给搜索流；未被消费的候选在这里释放。
            // The handle is handed over to the search stream when the candidate is actually opened; a candidate that is
            // not consumed is released here.
            if (entry.Handle == null || entry.Status.IsDirectory || !entry.Status.IsRegularFile)
            {
                entry.Dispose();
                continue;
            }

            var candidate = entry;
            yield return new SearchCandidate(
                entry.Path,
                entry.Path[searchPrefix..],
                entry.Status.Size,
                () => new FileStream(candidate.TakeHandle(), FileAccess.Read)
            );
            candidate.Dispose();
        }
    }

    /// <summary>
    /// 通过目录句柄枚举条目，每个条目以 O_NOFOLLOW 打开以读取类型、大小和修改时间；符号链接不打开（Handle 为 null），递归时跳过。
    /// Enumerates entries through the directory handle, opening each one with O_NOFOLLOW to read its type, size and
    /// modification time; a symbolic link is not opened (Handle is null) and is skipped when recursing.
    /// </summary>
    private static IEnumerable<DirectoryEntryHandle> EnumerateEntries(
        SafeFileHandle directory,
        string directoryPath,
        bool recursive,
        HashSet<string>? excludedDirectoryNames,
        CancellationToken ct
    )
    {
        foreach (var (name, type) in ReadEntries(directory))
        {
            ct.ThrowIfCancellationRequested();
            var entryPath = directoryPath.Length == 0 ? name : directoryPath + "/" + name;
            if (type == UnixNative.DT_LNK)
            {
                if (!recursive)
                {
                    yield return new DirectoryEntryHandle(name, entryPath, null, default);
                }

                continue;
            }

            var fd = UnixNative.OpenAt(
                UnixNative.Fd(directory),
                name,
                UnixNative.O_RDONLY | UnixNative.O_NONBLOCK | UnixNative.O_NOFOLLOW | UnixNative.O_CLOEXEC
            );
            if (fd < 0)
            {
                var errno = UnixNative.LastError;
                if (errno == UnixNative.ELOOP && !recursive)
                {
                    yield return new DirectoryEntryHandle(name, entryPath, null, default);
                }

                continue;
            }

            var handle = UnixNative.WrapFd(fd);
            var status = UnixNative.Stat(handle);
            var entry = new DirectoryEntryHandle(name, entryPath, handle, status);
            // 递归时先产出子目录的内容，再产出该目录本身，目录句柄在此期间保持打开。
            // When recursing, the contents of a subdirectory are yielded before the directory itself, and its handle
            // stays open meanwhile.
            if (recursive && status.IsDirectory && excludedDirectoryNames?.Contains(name) != true)
            {
                foreach (var child in EnumerateEntries(handle, entryPath, recursive, excludedDirectoryNames, ct))
                {
                    yield return child;
                }
            }

            yield return entry;
        }
    }

    /// <summary>
    /// 用 fdopendir 读取目录句柄的全部条目名与类型；先读完再返回，避免枚举期间目录被修改。
    /// Reads every entry name and type of the directory handle with fdopendir, finishing the read before returning so
    /// the directory can be modified afterwards.
    /// </summary>
    private static List<(string Name, int Type)> ReadEntries(SafeFileHandle directory)
    {
        var fd = UnixNative.OpenAt(
            UnixNative.Fd(directory),
            ".",
            UnixNative.O_RDONLY | UnixNative.O_DIRECTORY | UnixNative.O_CLOEXEC
        );
        if (fd < 0)
        {
            throw UnixNative.CreateException(UnixNative.LastError, "Cannot open directory for enumeration");
        }

        var stream = UnixNative.FdOpenDir(fd);
        if (stream == IntPtr.Zero)
        {
            var errno = UnixNative.LastError;
            UnixNative.WrapFd(fd).Dispose();
            throw UnixNative.CreateException(errno, "fdopendir failed");
        }

        var entries = new List<(string, int)>();
        try
        {
            while (true)
            {
                var result = UnixNative.ReadDir(stream, out var entry);
                if (result == -1)
                {
                    return entries;
                }

                if (result != 0)
                {
                    throw UnixNative.CreateException(result, "readdir failed");
                }

                var name =
                    entry.NameLength < 0
                        ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(entry.Name)
                        : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(entry.Name, entry.NameLength);
                if (name is null or "." or "..")
                {
                    continue;
                }

                entries.Add((name, entry.InodeType));
            }
        }
        finally
        {
            UnixNative.CloseDir(stream);
        }
    }

    private static void DeleteContents(SafeFileHandle directory, CancellationToken ct)
    {
        var fd = UnixNative.Fd(directory);
        foreach (var (name, type) in ReadEntries(directory))
        {
            ct.ThrowIfCancellationRequested();
            if (type != UnixNative.DT_LNK && UnixNative.UnlinkAt(fd, name, 0) == 0)
            {
                continue;
            }

            if (type == UnixNative.DT_LNK)
            {
                if (UnixNative.UnlinkAt(fd, name, 0) != 0 && UnixNative.LastError != UnixNative.ENOENT)
                {
                    throw UnixNative.CreateException(UnixNative.LastError, $"Cannot delete '{name}'");
                }

                continue;
            }

            var errno = UnixNative.LastError;
            if (errno == UnixNative.ENOENT)
            {
                continue;
            }

            if (errno is not (UnixNative.EISDIR or UnixNative.EPERM))
            {
                throw UnixNative.CreateException(errno, $"Cannot delete '{name}'");
            }

            using (var child = OpenChildDirectory(directory, name))
            {
                if (child == null)
                {
                    continue;
                }

                DeleteContents(child, ct);
            }

            if (
                UnixNative.UnlinkAt(fd, name, UnixNative.AT_REMOVEDIR) != 0
                && UnixNative.LastError != UnixNative.ENOENT
            )
            {
                throw UnixNative.CreateException(UnixNative.LastError, $"Cannot delete '{name}'");
            }
        }
    }

    /// <summary>
    /// 以 O_NOFOLLOW 打开子目录；名称不是目录（含符号链接）或不存在时返回 null。
    /// Opens a child directory with O_NOFOLLOW; returns null when the name is not a directory (including a symbolic
    /// link) or does not exist.
    /// </summary>
    private static SafeFileHandle? OpenChildDirectory(SafeFileHandle directory, string name)
    {
        var fd = UnixNative.OpenAt(
            UnixNative.Fd(directory),
            name,
            UnixNative.O_RDONLY | UnixNative.O_DIRECTORY | UnixNative.O_NOFOLLOW | UnixNative.O_CLOEXEC
        );
        if (fd >= 0)
        {
            return UnixNative.WrapFd(fd);
        }

        var errno = UnixNative.LastError;
        return errno == UnixNative.ENOENT || errno == UnixNative.ENOTDIR || errno == UnixNative.ELOOP
            ? null
            : throw UnixNative.CreateException(errno, $"Cannot open directory '{name}'");
    }

    private Walk Locate(string path, bool createDirectories)
    {
        var segments = NormalizeSegments(path);
        var walk = new Walk(this, segments, createDirectories);
        try
        {
            walk.ResolveParent();
            return walk;
        }
        catch
        {
            walk.Dispose();
            throw;
        }
    }

    private static string RequireFileName(Walk walk) =>
        walk.FinalName ?? throw new AgwException(ErrorCodes.FilePathRequired, "A file path must not be empty.");

    /// <summary>
    /// 创建类操作要求父目录已被完整解析到允许的目录内；中间路径段不是目录时不能退而在别处创建。
    /// Creating operations require the parent to be fully resolved inside the allowed directories; when an intermediate
    /// segment is not a directory, nothing may be created elsewhere instead.
    /// </summary>
    private static void RequireParent(Walk walk, string path)
    {
        walk.RequireInside(path);
        if (!walk.Found)
        {
            throw new AgwException(ErrorCodes.DirectoryNotFound, $"The parent directory of '{path}' was not found.");
        }
    }

    private static SafeFileHandle OpenExistingFile(Walk walk, int flags, string path)
    {
        RequireFileName(walk);
        var opened = OpenFinal(walk, flags | UnixNative.O_NONBLOCK, path);
        if (opened.Handle == null || !opened.Status.IsRegularFile)
        {
            opened.Dispose();
            throw new AgwException(ErrorCodes.FileNotFound, $"File '{path}' was not found.");
        }

        return opened.Handle;
    }

    /// <summary>
    /// 打开已有文件；不存在时在已解析的父目录句柄下用 O_CREAT|O_EXCL|O_NOFOLLOW 创建，创建既不经过路径也不跟随符号链接，
    /// 随后通过同一个句柄写入。<paramref name="mustCreate"/> 为 true 且文件已存在时返回 null。
    /// Opens an existing file; when it is missing, creates it under the resolved parent directory handle with
    /// O_CREAT|O_EXCL|O_NOFOLLOW, so creation neither goes through a path nor follows a symbolic link, and writes through
    /// the same handle afterwards. Returns null when <paramref name="mustCreate"/> is true and the file already exists.
    /// </summary>
    private static SafeFileHandle? OpenOrCreateFile(Walk walk, string path, bool mustCreate)
    {
        while (true)
        {
            var opened = OpenFinal(walk, UnixNative.O_RDWR | UnixNative.O_NONBLOCK, path);
            if (opened.Handle != null)
            {
                if (!opened.Status.IsRegularFile)
                {
                    opened.Dispose();
                    throw new AgwException(ErrorCodes.InvalidParam, $"'{path}' is not a regular file.");
                }

                if (!mustCreate)
                {
                    return opened.Handle;
                }

                opened.Dispose();
                return null;
            }

            if (opened.Failure != OpenFailure.Missing)
            {
                throw new AgwException(ErrorCodes.FileNotFound, $"File '{path}' was not found.");
            }

            RequireParent(walk, path);
            var name = RequireFileName(walk);
            var fd = UnixNative.OpenAtCreate(
                UnixNative.Fd(walk.Directory),
                name,
                UnixNative.O_RDWR
                    | UnixNative.O_EXCL
                    | UnixNative.O_NOFOLLOW
                    | UnixNative.O_NONBLOCK
                    | UnixNative.O_CLOEXEC,
                FileMode
            );
            if (fd >= 0)
            {
                return UnixNative.WrapFd(fd);
            }

            var errno = UnixNative.LastError;
            if (errno == UnixNative.EEXIST)
            {
                if (mustCreate)
                {
                    return null;
                }

                continue;
            }

            throw UnixNative.CreateException(errno, $"Cannot create file '{path}'");
        }
    }

    /// <summary>
    /// 在父目录句柄下用 O_NOFOLLOW 打开最终名称；名称是符号链接时沿链接继续解析后重试。名称为空表示根目录本身。
    /// Opens the final name under the parent directory handle with O_NOFOLLOW, following and retrying when the name is a
    /// symbolic link. An empty name means the root itself.
    /// </summary>
    private static OpenedFile OpenFinal(Walk walk, int flags, string path)
    {
        while (true)
        {
            // 最后一段是 "." 或 ".."（只可能来自链接目标），或者当前目录还不在允许范围内时，先把最后一段当作目录进入，
            // 由 Step 负责父目录回退与根目录身份判断；进入之后最终对象就是当前目录本身。
            // When the last segment is "." or ".." (only possible from a link target), or the current directory is not
            // inside an allowed root yet, enter the last segment as a directory first so Step handles parent moves and
            // root identity; afterwards the final object is the current directory itself.
            if (walk.Found && walk.FinalName != null && (walk.FinalName is "." or ".." || !walk.Inside))
            {
                walk.EnterFinal();
                continue;
            }

            walk.RequireInside(path);
            if (!walk.Found)
            {
                return new OpenedFile(OpenFailure.Missing);
            }

            var fd = UnixNative.OpenAt(
                UnixNative.Fd(walk.Directory),
                walk.FinalName ?? ".",
                flags | UnixNative.O_NOFOLLOW | UnixNative.O_CLOEXEC
            );
            if (fd >= 0)
            {
                var handle = UnixNative.WrapFd(fd);
                return new OpenedFile(handle, UnixNative.Stat(handle));
            }

            // O_NOFOLLOW 遇到符号链接时 Linux 返回 ELOOP，macOS 在同时带 O_DIRECTORY 时返回 ENOTDIR；两种情况都按链接处理。
            // With O_NOFOLLOW a symbolic link yields ELOOP on Linux, and ENOTDIR on macOS when O_DIRECTORY is also set;
            // both cases are handled as a link.
            var errno = UnixNative.LastError;
            if ((errno == UnixNative.ELOOP || errno == UnixNative.ENOTDIR) && walk.FinalName != null)
            {
                if (walk.FollowFinalLink())
                {
                    continue;
                }

                if (errno == UnixNative.ELOOP)
                {
                    return new OpenedFile(OpenFailure.Missing);
                }
            }

            return errno switch
            {
                UnixNative.ENOENT => new OpenedFile(OpenFailure.Missing),
                UnixNative.ENOTDIR => new OpenedFile(OpenFailure.NotDirectory),
                UnixNative.EISDIR => new OpenedFile(OpenFailure.IsDirectory),
                _ => throw UnixNative.CreateException(errno, $"Cannot open '{path}'"),
            };
        }
    }

    /// <summary>
    /// 把根目录相对路径拆成路径段：拒绝绝对路径，跳过 "." 与空段，"." 与 ".." 只在文本层面折叠，越过根目录即拒绝。
    /// Splits a root-relative path into segments: rooted paths are rejected, "." and empty segments are skipped, and ".."
    /// is collapsed textually only, rejecting anything that climbs above the root.
    /// </summary>
    private static string[] NormalizeSegments(string path)
    {
        // NUL 会在 native 调用中截断字符串，使 "..\0x" 变成 ".."，必须在进入任何系统调用之前拒绝。
        // A NUL truncates the string inside native calls, turning "..\0x" into "..", so it is rejected before any system
        // call sees the path.
        if (path.Contains('\0'))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Path must not contain NUL characters.");
        }

        if (Path.IsPathRooted(path))
        {
            throw new AgwException(
                ErrorCodes.FilePathOutsideRoot,
                $"Path '{path}' must be relative to the file system root."
            );
        }

        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (segments.Count == 0)
                    {
                        throw new AgwException(
                            ErrorCodes.FilePathOutsideRoot,
                            $"Path '{path}' is outside the allowed root directory."
                        );
                    }

                    segments.RemoveAt(segments.Count - 1);
                    continue;
                default:
                    segments.Add(segment);
                    continue;
            }
        }

        return segments.ToArray();
    }

    private enum OpenFailure
    {
        None,
        Missing,
        NotDirectory,
        IsDirectory,
    }

    /// <summary>
    /// 最终打开的对象：句柄与其 fstat 结果，或失败原因。
    /// The finally opened object: its handle and fstat result, or the failure reason.
    /// </summary>
    private readonly struct OpenedFile : IDisposable
    {
        public OpenedFile(SafeFileHandle handle, UnixNative.FileStatus status)
        {
            Handle = handle;
            Status = status;
        }

        public OpenedFile(OpenFailure failure)
        {
            Failure = failure;
        }

        public SafeFileHandle? Handle { get; }
        public UnixNative.FileStatus Status { get; }
        public OpenFailure Failure { get; }

        public void Dispose() => Handle?.Dispose();
    }

    /// <summary>
    /// 枚举得到的条目：名称、相对根目录的路径，以及已打开的句柄与其状态（符号链接没有句柄）。
    /// An enumerated entry: its name, root-relative path, and the opened handle with its status (a symbolic link has no
    /// handle).
    /// </summary>
    private sealed class DirectoryEntryHandle : IDisposable
    {
        private SafeFileHandle? _handle;

        public DirectoryEntryHandle(string name, string path, SafeFileHandle? handle, UnixNative.FileStatus status)
        {
            Name = name;
            Path = path;
            _handle = handle;
            Status = status;
        }

        public string Name { get; }
        public string Path { get; }
        public SafeFileHandle? Handle => _handle;
        public UnixNative.FileStatus Status { get; }

        public SafeFileHandle TakeHandle() =>
            Interlocked.Exchange(ref _handle, null)
            ?? throw new AgwException(ErrorCodes.FileNotFound, $"File '{Path}' is no longer available for reading.");

        public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
    }

    /// <summary>
    /// 一次路径解析的状态：当前目录句柄、是否位于允许的根目录内、根目录内的深度、已跟随的链接数，以及尚未处理的路径段。
    /// 中间目录以 O_NOFOLLOW 打开；遇到符号链接时读取目标并把它的路径段插到队列前面，绝对目标从 "/" 重新开始并等待再次
    /// 进入某个允许的根目录（以设备号与 inode 判断）。
    /// The state of one path resolution: the current directory handle, whether it lies inside an allowed root, the depth
    /// inside that root, the number of links followed, and the segments still pending. Intermediate directories are
    /// opened with O_NOFOLLOW; a symbolic link is read and its segments are queued in front, and an absolute target
    /// restarts from "/" until an allowed root is entered again (decided by device and inode).
    /// </summary>
    private sealed class Walk : IDisposable
    {
        private readonly UnixConfinedFileSystem _fileSystem;
        private readonly bool _createDirectories;
        private readonly string[] _pathSegments;
        private SafeFileHandle _directory;
        private Queue<string> _pending;
        private bool _inside;
        private int _depth;
        private int _hops;

        public Walk(UnixConfinedFileSystem fileSystem, string[] pathSegments, bool createDirectories)
        {
            _fileSystem = fileSystem;
            _createDirectories = createDirectories;
            _pathSegments = pathSegments;
            _pending = new Queue<string>([.. fileSystem._prefix, .. pathSegments]);
            var fd = UnixNative.Open(
                fileSystem._rootPath,
                UnixNative.O_RDONLY | UnixNative.O_DIRECTORY | UnixNative.O_CLOEXEC
            );
            if (fd < 0)
            {
                throw new AgwException(
                    ErrorCodes.ResourceNotFound,
                    $"Project directory is unavailable: '{fileSystem._rootPath}'."
                );
            }

            RootDirectory = UnixNative.WrapFd(fd);
            _directory = RootDirectory;
            Found = true;
            CheckIdentity();
        }

        public SafeFileHandle RootDirectory { get; }
        public SafeFileHandle Directory => _directory;
        public bool Found { get; private set; }
        public string? FinalName => _pending.Count == 0 ? null : _pending.Peek();
        public string RelativePath => string.Join('/', _pathSegments);
        public string FullPath =>
            Path.Combine(_fileSystem._rootPath, string.Join('/', _fileSystem._prefix), RelativePath);

        public void RequireInside(string path)
        {
            if (!_inside)
            {
                throw new AgwException(
                    ErrorCodes.FilePathOutsideRoot,
                    $"Path '{path}' resolves outside the Project directories."
                );
            }
        }

        /// <summary>
        /// 解析到只剩最后一个路径段；中间目录缺失且不需要创建时 Found 变为 false。
        /// Resolves until only the last segment remains; Found becomes false when an intermediate directory is missing
        /// and must not be created.
        /// </summary>
        public void ResolveParent()
        {
            while (Found && _pending.Count > 1)
            {
                Found = Step(_pending.Dequeue());
            }
        }

        public bool Inside => _inside;

        /// <summary>
        /// 把最后一个路径段当作目录进入：符号链接会被跟随，"." 与 ".." 按目录移动处理，进入允许的根目录时更新位置状态。
        /// Enters the last segment as a directory: a symbolic link is followed, "." and ".." are handled as directory
        /// moves, and entering an allowed root updates the position state.
        /// </summary>
        public void EnterFinal()
        {
            if (Found && _pending.Count > 0)
            {
                Found = Step(_pending.Dequeue());
            }

            ResolveParent();
        }

        /// <summary>
        /// 最后一个路径段若是符号链接，读取目标并继续解析，使新的最后一段成为链接目标的最后一段；不是链接时返回 false 且
        /// 状态不变。
        /// When the last segment is a symbolic link, reads its target and keeps resolving so the new last segment is the
        /// last segment of the target; returns false and leaves the state untouched when it is not a link.
        /// </summary>
        public bool FollowFinalLink()
        {
            var target = UnixNative.ReadLink(_directory, _pending.Peek());
            if (target == null)
            {
                return false;
            }

            _pending.Dequeue();
            Splice(target);
            ResolveParent();
            return true;
        }

        private bool Step(string segment)
        {
            switch (segment)
            {
                case ".":
                    return true;
                case "..":
                    MoveToParent();
                    return true;
            }

            while (true)
            {
                var fd = UnixNative.OpenAt(
                    UnixNative.Fd(_directory),
                    segment,
                    UnixNative.O_RDONLY | UnixNative.O_DIRECTORY | UnixNative.O_NOFOLLOW | UnixNative.O_CLOEXEC
                );
                if (fd >= 0)
                {
                    Replace(UnixNative.WrapFd(fd));
                    if (_inside)
                    {
                        _depth++;
                    }

                    CheckIdentity();
                    return true;
                }

                // O_NOFOLLOW 遇到符号链接时 Linux 返回 ELOOP，macOS 在同时带 O_DIRECTORY 时返回 ENOTDIR；两种情况都按链接处理。
                // With O_NOFOLLOW a symbolic link yields ELOOP on Linux, and ENOTDIR on macOS when O_DIRECTORY is also
                // set; both cases are handled as a link.
                var errno = UnixNative.LastError;
                if (errno == UnixNative.ELOOP || errno == UnixNative.ENOTDIR)
                {
                    var target = UnixNative.ReadLink(_directory, segment);
                    if (target == null)
                    {
                        return false;
                    }

                    Splice(target);
                    return true;
                }

                if (errno != UnixNative.ENOENT)
                {
                    throw UnixNative.CreateException(errno, $"Cannot open directory '{segment}'");
                }

                if (!_createDirectories || !_inside)
                {
                    return false;
                }

                if (
                    UnixNative.MkDirAt(UnixNative.Fd(_directory), segment, DirectoryMode) != 0
                    && UnixNative.LastError != UnixNative.EEXIST
                )
                {
                    throw UnixNative.CreateException(UnixNative.LastError, $"Cannot create directory '{segment}'");
                }
            }
        }

        /// <summary>
        /// 把符号链接目标的路径段插到待处理队列前面；绝对目标从 "/" 重新开始解析。
        /// Queues the segments of a symbolic link target in front of the pending ones; an absolute target restarts from "/".
        /// </summary>
        private void Splice(string target)
        {
            if (++_hops > MaxSymbolicLinkHops)
            {
                throw new AgwException(
                    ErrorCodes.FilePathOutsideRoot,
                    $"The path follows more than {MaxSymbolicLinkHops} symbolic links."
                );
            }

            if (target.StartsWith('/'))
            {
                var fd = UnixNative.Open("/", UnixNative.O_RDONLY | UnixNative.O_DIRECTORY | UnixNative.O_CLOEXEC);
                if (fd < 0)
                {
                    throw UnixNative.CreateException(UnixNative.LastError, "Cannot open '/'");
                }

                Replace(UnixNative.WrapFd(fd));
                _inside = false;
                _depth = 0;
                CheckIdentity();
            }

            var targetSegments = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
            _pending = new Queue<string>([.. targetSegments, .. _pending]);
        }

        private void MoveToParent()
        {
            var fd = UnixNative.OpenAt(
                UnixNative.Fd(_directory),
                "..",
                UnixNative.O_RDONLY | UnixNative.O_DIRECTORY | UnixNative.O_NOFOLLOW | UnixNative.O_CLOEXEC
            );
            if (fd < 0)
            {
                throw UnixNative.CreateException(UnixNative.LastError, "Cannot open parent directory");
            }

            Replace(UnixNative.WrapFd(fd));
            if (_inside && --_depth < 0)
            {
                _inside = false;
                _depth = 0;
            }

            CheckIdentity();
        }

        private void CheckIdentity()
        {
            var identity = UnixNative.Stat(_directory).Identity;
            foreach (var root in _fileSystem._allowedRoots)
            {
                if (root == identity)
                {
                    _inside = true;
                    _depth = 0;
                    return;
                }
            }
        }

        private void Replace(SafeFileHandle next)
        {
            if (!ReferenceEquals(_directory, RootDirectory))
            {
                _directory.Dispose();
            }

            _directory = next;
        }

        public void Dispose()
        {
            Replace(RootDirectory);
            RootDirectory.Dispose();
        }
    }
}
