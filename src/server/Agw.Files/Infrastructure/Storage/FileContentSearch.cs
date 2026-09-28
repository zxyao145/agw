using System.Buffers;
using System.Text.RegularExpressions;
using Agw.Files.Abstracts.Dtos;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Agw.Files.Infrastructure.Storage;

/// <summary>
/// 待搜索的文件：相对文件系统根目录的路径、相对搜索目录的路径（用于 glob 匹配）、枚举时读到的长度，以及打开只读流的方法。
/// A file to search: its path relative to the file system root, its path relative to the search directory (for glob
/// matching), the length read while enumerating, and a way to open a read-only stream.
/// </summary>
internal readonly record struct SearchCandidate(
    string FileName,
    string SearchRelativePath,
    long Length,
    Func<Stream> Open
);

/// <summary>
/// 文件内容搜索的公共部分：正则编译、上限检查、glob 与扩展名过滤，以及逐文件的按行匹配；文件的枚举与打开由调用方提供。
/// The shared part of file content search: regex compilation, limit checks, glob and extension filters, and per-file
/// line matching; enumerating and opening files is up to the caller.
/// </summary>
internal static class FileContentSearch
{
    private static readonly TimeSpan SearchRegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 内容搜索整体读入内存的文件大小上限；更大的文件逐行读取。
    /// The size limit for reading a file into memory during content search; larger files are read line by line.
    /// </summary>
    private const long MaxBufferedSearchFileBytes = 16 * 1024 * 1024;

    public static async IAsyncEnumerable<AgwFileSearchResult> SearchAsync(
        IEnumerable<SearchCandidate> files,
        SearchOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
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

        // 扩展名直接用 ReadOnlySpan<char> 查找，每个条目不再分配字符串。
        // Extensions are looked up with ReadOnlySpan<char>, allocating no string per entry.
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
            foreach (var file in files)
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

                if (includeExtensions is { } extensions && !HasIncludedExtension(file.FileName, extensions))
                {
                    continue;
                }

                if (matcher?.Match(file.SearchRelativePath).HasMatches == false)
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

                Stream stream;
                try
                {
                    stream = file.Open();
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                var matches = new List<AgwFileSearchMatch>();
                bool stopSearch;
                using (var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true))
                {
                    if (file.Length > MaxBufferedSearchFileBytes)
                    {
                        // 超出缓冲上限的文件逐行读取，内存占用与行长度相关。
                        // Files beyond the buffer limit are read line by line, so memory follows the line length.
                        stopSearch = await CollectStreamedMatchesAsync(
                                reader,
                                regex,
                                options.MaxHits - hitCount,
                                matches,
                                ct
                            )
                            .ConfigureAwait(false);
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
                            length = await reader
                                .ReadBlockAsync(buffer.AsMemory(0, capacity), ct)
                                .ConfigureAwait(false);
                        }
                        catch (IOException)
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
                }

                hitCount += matches.Count;
                if (matches.Count > 0)
                {
                    yield return new AgwFileSearchResult
                    {
                        FileName = file.FileName,
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
}
