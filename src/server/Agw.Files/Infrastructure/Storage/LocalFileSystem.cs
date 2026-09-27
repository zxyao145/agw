using System.Buffers;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Agw.Files.Infrastructure.Storage;

public sealed class LocalFileSystem : ILocalFileSystem
{
    private static readonly TimeSpan SearchRegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 内容搜索整体读入内存的文件大小上限；更大的文件逐行读取。
    /// The size limit for reading a file into memory during content search; larger files are read line by line.
    /// </summary>
    private const long MaxBufferedSearchFileBytes = 16 * 1024 * 1024;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// 按完整路径哈希选取的写锁，同一进程内不同实例（包括嵌套根目录）对同一文件的写入与读改写操作互斥。
    /// Write locks selected by full-path hash, so writes and read-modify-write operations on the same file are mutually
    /// exclusive across instances in the process, including nested roots.
    /// </summary>
    private static readonly SemaphoreSlim[] PathLocks = Enumerable
        .Range(0, 64)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();

    private static readonly StringComparer PathLockComparer = StringComparer.FromComparison(PathComparison);

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
        using var pathLock = await AcquirePathLockAsync(fullPath, ct).ConfigureAwait(false);
        await WriteFileAsync(fullPath, content, ct).ConfigureAwait(false);
    }

    public async Task<bool> CreateTextFileAsync(string path, string content, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        using var pathLock = await AcquirePathLockAsync(fullPath, ct).ConfigureAwait(false);
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
        using var pathLock = await AcquirePathLockAsync(fullPath, ct).ConfigureAwait(false);
        var content = await ReadExistingFileAsync(path, fullPath, ct).ConfigureAwait(false);
        var (newContent, count) = TextContentEditor.ApplyReplace(content, oldString, newString, replaceAll);
        await WriteFileAsync(fullPath, newContent, ct).ConfigureAwait(false);
        return count;
    }

    public async Task ReplaceLinesAsync(string path, IReadOnlyList<AgwFileLineEdit> edits, CancellationToken ct)
    {
        var fullPath = ResolveFilePath(path);
        using var pathLock = await AcquirePathLockAsync(fullPath, ct).ConfigureAwait(false);
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
        using var pathLock = await AcquirePathLockAsync(fullPath, ct).ConfigureAwait(false);

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

        var regexOptions = RegexOptions.Compiled;
        if (options.CaseInsensitive)
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }
        if (options.Multiline)
        {
            regexOptions |= RegexOptions.Singleline;
        }

        Regex regex;
        try
        {
            regex = new Regex(options.Pattern, regexOptions, SearchRegexTimeout);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        if (
            options.MaxHits is <= 0
            || options.MaxFiles is <= 0
            || options.MaxFileSizeBytes is <= 0
            || options.MaxTotalBytes is <= 0
        )
        {
            yield break;
        }

        Matcher? matcher = null;
        if (!string.IsNullOrWhiteSpace(options.FilenameGlob))
        {
            matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(options.FilenameGlob);
        }

        // 目录名与扩展名直接用 ReadOnlySpan<char> 查找，每个条目不再分配字符串。
        // Directory names and extensions are looked up with ReadOnlySpan<char>, allocating no string per entry.
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>>? excludedDirectoryNames = null;
        if (options.ExcludedDirectoryNames is { Count: > 0 })
        {
            excludedDirectoryNames = new HashSet<string>(
                options.ExcludedDirectoryNames,
                StringComparer.OrdinalIgnoreCase
            ).GetAlternateLookup<ReadOnlySpan<char>>();
        }

        HashSet<string>.AlternateLookup<ReadOnlySpan<char>>? includeExtensions = null;
        if (options.IncludeExtensions is { Count: > 0 })
        {
            includeExtensions = new HashSet<string>(
                options.IncludeExtensions,
                StringComparer.Ordinal
            ).GetAlternateLookup<ReadOnlySpan<char>>();
        }

        var hitCount = 0;
        var fileCount = 0;
        long totalBytes = 0;
        char[]? buffer = null;

        try
        {
            foreach (var file in EnumerateSearchFiles(fullPath, options.Recursive, excludedDirectoryNames, ct))
            {
                ct.ThrowIfCancellationRequested();

                if (options.MaxHits.HasValue && hitCount >= options.MaxHits.Value)
                {
                    yield break;
                }

                if (options.MaxFiles.HasValue && fileCount >= options.MaxFiles.Value)
                {
                    yield break;
                }

                if (includeExtensions is { } extensions && !HasIncludedExtension(file.FullPath, extensions))
                {
                    continue;
                }

                var searchRelativePath = Path.GetRelativePath(fullPath, file.FullPath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (matcher?.Match(searchRelativePath).HasMatches == false)
                {
                    continue;
                }

                if (options.MaxFileSizeBytes.HasValue && file.Length > options.MaxFileSizeBytes.Value)
                {
                    continue;
                }

                if (options.MaxTotalBytes.HasValue && (file.Length > options.MaxTotalBytes.Value - totalBytes))
                {
                    yield break;
                }

                fileCount++;
                totalBytes += file.Length;

                var matches = new List<AgwFileSearchMatch>();
                bool stopSearch;
                if (file.Length > MaxBufferedSearchFileBytes)
                {
                    // 超出缓冲上限的文件逐行读取，内存占用与行长度相关。
                    // Files beyond the buffer limit are read line by line, so memory follows the line length.
                    StreamReader reader;
                    try
                    {
                        reader = new StreamReader(file.FullPath, detectEncodingFromByteOrderMarks: true);
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    using (reader)
                    {
                        stopSearch = await CollectStreamedMatchesAsync(
                                reader,
                                regex,
                                options.MaxHits - hitCount,
                                matches,
                                ct
                            )
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    // UTF-8 解码后的字符数不超过字节数，文件整体读入 ArrayPool 缓冲区，逐行匹配时只为命中的行分配字符串。
                    // Decoded UTF-8 never has more chars than bytes, so the file is read into an ArrayPool buffer and only matching lines allocate strings.
                    var capacity = (int)Math.Max(file.Length, 1);
                    if (buffer == null || buffer.Length < capacity)
                    {
                        if (buffer != null)
                        {
                            ArrayPool<char>.Shared.Return(buffer);
                        }
                        buffer = ArrayPool<char>.Shared.Rent(capacity);
                    }

                    int length;
                    try
                    {
                        using var reader = new StreamReader(file.FullPath, detectEncodingFromByteOrderMarks: true);
                        length = await reader.ReadBlockAsync(buffer.AsMemory(0, capacity), ct).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    stopSearch = CollectMatches(
                        buffer.AsSpan(0, length),
                        regex,
                        options.MaxHits - hitCount,
                        matches,
                        ct
                    );
                }

                hitCount += matches.Count;
                if (matches.Count > 0)
                {
                    yield return new AgwFileSearchResult
                    {
                        FileName = ToRelativePath(file.FullPath),
                        Snippet = matches[0].Line,
                        MatchingLines = matches,
                    };
                }

                if (stopSearch)
                {
                    yield break;
                }
            }
        }
        finally
        {
            if (buffer != null)
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// 逐行读取超出缓冲上限的文件并收集命中；遇到含 '\0' 的行按二进制文件停止。返回整个搜索是否应当停止（正则超时或达到命中上限）。
    /// Reads a file beyond the buffer limit line by line and collects matches; a line containing '\0' stops the file as
    /// binary. Returns whether the whole search should stop (regex timeout or hit limit reached).
    /// </summary>
    private static async Task<bool> CollectStreamedMatchesAsync(
        StreamReader reader,
        Regex regex,
        int? remainingHits,
        List<AgwFileSearchMatch> matches,
        CancellationToken cancellationToken
    )
    {
        var lineNumber = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return false;
            }

            if (line == null || line.Contains('\0'))
            {
                return false;
            }

            lineNumber++;
            bool isMatch;
            try
            {
                isMatch = regex.IsMatch(line);
            }
            catch (RegexMatchTimeoutException)
            {
                return true;
            }

            if (!isMatch)
            {
                continue;
            }

            if (remainingHits.HasValue && matches.Count >= remainingHits.Value)
            {
                return true;
            }

            matches.Add(new AgwFileSearchMatch { LineNumber = lineNumber, Line = line });
        }
    }

    /// <summary>
    /// 在内存中的文件内容里按 StreamReader.ReadLine 的换行规则逐行匹配；遇到含 '\0' 的行按二进制文件停止。返回是否发生了正则超时。
    /// Matches in-memory file content line by line with StreamReader.ReadLine's line breaks; a line containing '\0' stops the file as binary. Returns whether the regex timed out.
    /// </summary>
    private static bool CollectMatches(
        ReadOnlySpan<char> content,
        Regex regex,
        int? remainingHits,
        List<AgwFileSearchMatch> matches,
        CancellationToken cancellationToken
    )
    {
        var lineNumber = 0;
        while (!content.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lineEnd = content.IndexOfAny('\r', '\n');
            ReadOnlySpan<char> line;
            if (lineEnd < 0)
            {
                line = content;
                content = [];
            }
            else
            {
                line = content[..lineEnd];
                var separatorLength =
                    content[lineEnd] == '\r' && lineEnd + 1 < content.Length && content[lineEnd + 1] == '\n' ? 2 : 1;
                content = content[(lineEnd + separatorLength)..];
            }

            if (line.Contains('\0'))
            {
                return false;
            }

            lineNumber++;
            bool isMatch;
            try
            {
                isMatch = regex.IsMatch(line);
            }
            catch (RegexMatchTimeoutException)
            {
                return true;
            }

            if (!isMatch)
            {
                continue;
            }

            if (remainingHits.HasValue && matches.Count >= remainingHits.Value)
            {
                return false;
            }

            matches.Add(new AgwFileSearchMatch { LineNumber = lineNumber, Line = line.ToString() });
        }

        return false;
    }

    private static bool HasIncludedExtension(
        string path,
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> extensions
    )
    {
        const int StackLimit = 256;
        var extension = Path.GetExtension(path.AsSpan()).TrimStart('.');
        if (extension.Length > StackLimit)
        {
            return extensions.Contains(extension.ToString().ToLowerInvariant());
        }

        Span<char> lowered = stackalloc char[StackLimit];
        var written = extension.ToLowerInvariant(lowered);
        return extensions.Contains(lowered[..written]);
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

    private static async Task<PathLock> AcquirePathLockAsync(string fullPath, CancellationToken ct)
    {
        var semaphore = PathLocks[(int)((uint)PathLockComparer.GetHashCode(fullPath) % (uint)PathLocks.Length)];
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new PathLock(semaphore);
    }

    /// <summary>
    /// 已获取的路径写锁，释放时归还信号量。
    /// An acquired path write lock that returns its semaphore when disposed.
    /// </summary>
    private readonly struct PathLock : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;

        public PathLock(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose() => _semaphore.Release();
    }

    /// <summary>
    /// 枚举得到的待搜索文件：完整路径与枚举时读到的长度。
    /// A file to search from the enumeration: its full path and the length read while enumerating.
    /// </summary>
    private readonly record struct SearchFile(string FullPath, long Length);
}
