using System.IO.Enumeration;
using System.Text.RegularExpressions;
using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Files.Exceptions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Agw.Files.Application.Storage.Local;

public sealed class LocalFileSystem : IAgwFileSystem
{
    private static readonly TimeSpan SearchRegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly string _rootPath;
    private readonly string _normalizedRoot;

    public LocalFileSystem(string rootPath)
    {
        _rootPath = rootPath;
        _normalizedRoot =
            Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
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
            throw new AgwFilesException(
                FilesErrorCode.PathOutsideRoot,
                $"Path '{path}' must be relative to the file system root."
            );
        }

        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, normalized));

        if (!fullPath.StartsWith(_normalizedRoot, PathComparison))
        {
            throw new AgwFilesException(
                FilesErrorCode.PathOutsideRoot,
                $"Path '{path}' is outside the allowed root directory."
            );
        }

        return fullPath;
    }

    internal string ResolvePhysicalPath(string path)
    {
        return ResolvePath(path);
    }

    internal string GetRelativePath(string fullPath)
    {
        return ToRelativePath(fullPath);
    }

    private string ToRelativePath(string fullPath)
    {
        var rootFullPath = Path.GetFullPath(_rootPath);
        if (fullPath.Equals(rootFullPath, StringComparison.Ordinal))
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
        var fullPath = ResolvePath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllTextAsync(fullPath, content, ct);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolvePath(path);
        Directory.CreateDirectory(fullPath);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        var fullPath = ResolvePath(path);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
        else if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }

        return Task.CompletedTask;
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

        var entries = Directory.EnumerateFileSystemEntries(
            fullPath,
            searchPattern,
            new EnumerationOptions
            {
                AttributesToSkip = recursive ? FileAttributes.ReparsePoint : (FileAttributes)0,
                IgnoreInaccessible = true,
                RecurseSubdirectories = recursive,
                ReturnSpecialDirectories = false,
            }
        );

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = ToRelativePath(entry);
            bool isDir = Directory.Exists(entry);

            if (isDir)
            {
                var dirInfo = new DirectoryInfo(entry);
                yield return new FileEntry(
                    Path: relativePath,
                    IsDirectory: true,
                    Size: 0,
                    LastModifiedUtc: dirInfo.LastWriteTimeUtc
                );
            }
            else
            {
                var fileInfo = new FileInfo(entry);
                yield return new FileEntry(
                    Path: relativePath,
                    IsDirectory: false,
                    Size: fileInfo.Length,
                    LastModifiedUtc: fileInfo.LastWriteTimeUtc
                );
            }
        }
    }

    public async IAsyncEnumerable<SearchHit> SearchAsync(
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

        HashSet<string>? excludedDirectoryNames = null;
        if (options.ExcludedDirectoryNames is { Count: > 0 })
        {
            excludedDirectoryNames = new HashSet<string>(
                options.ExcludedDirectoryNames,
                StringComparer.OrdinalIgnoreCase
            );
        }

        var hitCount = 0;
        var fileCount = 0;
        long totalBytes = 0;

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

            if (options.IncludeExtensions is { Count: > 0 })
            {
                var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
                if (!options.IncludeExtensions.Contains(ext))
                {
                    continue;
                }
            }

            var searchRelativePath = Path.GetRelativePath(fullPath, file).Replace(Path.DirectorySeparatorChar, '/');
            if (matcher?.Match(searchRelativePath).HasMatches == false)
            {
                continue;
            }

            long fileSize;
            try
            {
                fileSize = new FileInfo(file).Length;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (options.MaxFileSizeBytes.HasValue && fileSize > options.MaxFileSizeBytes.Value)
            {
                continue;
            }

            if (options.MaxTotalBytes.HasValue && (fileSize > options.MaxTotalBytes.Value - totalBytes))
            {
                yield break;
            }

            fileCount++;
            totalBytes += fileSize;

            var relativePath = ToRelativePath(file);
            StreamReader reader;
            try
            {
                reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
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
                var lineNumber = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        break;
                    }

                    if (line == null || line.Contains('\0'))
                    {
                        break;
                    }

                    lineNumber++;
                    bool isMatch;
                    var regexTimedOut = false;
                    try
                    {
                        isMatch = regex.IsMatch(line);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        isMatch = false;
                        regexTimedOut = true;
                    }

                    if (regexTimedOut)
                    {
                        yield break;
                    }

                    if (!isMatch)
                    {
                        continue;
                    }

                    if (options.MaxHits.HasValue && hitCount >= options.MaxHits.Value)
                    {
                        yield break;
                    }

                    hitCount++;
                    yield return new SearchHit(relativePath, lineNumber, line);
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateSearchFiles(
        string rootPath,
        bool recursive,
        HashSet<string>? excludedDirectoryNames,
        CancellationToken cancellationToken
    )
    {
        var files = new FileSystemEnumerable<string>(
            rootPath,
            static (ref FileSystemEntry entry) => entry.ToFullPath(),
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

        if (excludedDirectoryNames != null)
        {
            files.ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                !excludedDirectoryNames.Contains(entry.FileName.ToString());
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }
}
