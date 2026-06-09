using System.Text.RegularExpressions;

using Agw.Shared.Contracts.Storage;
using Agw.Shared.Exceptions;

namespace Agw.Files.Application.Storage.Local;

public sealed class LocalFileSystem : IAgwFileSystem
{
    private readonly string _rootPath;
    private readonly string _normalizedRoot;

    public LocalFileSystem(string rootPath)
    {
        _rootPath = rootPath;
        _normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public string NormalizedRoot => _normalizedRoot;

    private string ResolvePath(string path)
    {
        var normalized = path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.IsNullOrEmpty(normalized))
        {
            return Path.GetFullPath(_rootPath);
        }

        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, normalized));

        if (!fullPath.StartsWith(_normalizedRoot, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.PathOutsideRoot, $"Path '{path}' is outside the allowed root directory.");
        }

        return fullPath;
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
            return Task.FromResult<FileEntry?>(new FileEntry(
                Path: ToRelativePath(fullPath),
                IsDirectory: false,
                Size: info.Length,
                LastModifiedUtc: info.LastWriteTimeUtc));
        }

        if (Directory.Exists(fullPath))
        {
            var info = new DirectoryInfo(fullPath);
            return Task.FromResult<FileEntry?>(new FileEntry(
                Path: ToRelativePath(fullPath),
                IsDirectory: true,
                Size: 0,
                LastModifiedUtc: info.LastWriteTimeUtc));
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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var fullPath = ResolvePath(path);
        if (!Directory.Exists(fullPath))
        {
            yield break;
        }

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = Directory.GetFileSystemEntries(fullPath, searchPattern, option);

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
                    LastModifiedUtc: dirInfo.LastWriteTimeUtc);
            }
            else
            {
                var fileInfo = new FileInfo(entry);
                yield return new FileEntry(
                    Path: relativePath,
                    IsDirectory: false,
                    Size: fileInfo.Length,
                    LastModifiedUtc: fileInfo.LastWriteTimeUtc);
            }
        }
    }

    public async IAsyncEnumerable<SearchHit> SearchAsync(
        string rootPath,
        SearchOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
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
            regex = new Regex(options.Pattern, regexOptions);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        var allFiles = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories);
        var hitCount = 0;

        foreach (var file in allFiles)
        {
            ct.ThrowIfCancellationRequested();

            if (options.MaxHits.HasValue && hitCount >= options.MaxHits.Value)
            {
                yield break;
            }

            if (file.Contains("\\.git\\") || file.Contains("/.git/"))
            {
                continue;
            }

            if (options.IncludeExtensions is { Count: > 0 })
            {
                var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
                if (!options.IncludeExtensions.Contains(ext))
                {
                    continue;
                }
            }

            if (!string.IsNullOrEmpty(options.FilenameGlob))
            {
                var filename = Path.GetFileName(file);
                if (!MatchesSimpleGlob(filename, options.FilenameGlob))
                {
                    continue;
                }
            }

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, ct);
            }
            catch
            {
                continue;
            }

            var relativePath = ToRelativePath(file);

            for (int i = 0; i < lines.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                if (regex.IsMatch(lines[i]))
                {
                    if (options.MaxHits.HasValue && hitCount >= options.MaxHits.Value)
                    {
                        yield break;
                    }

                    hitCount++;
                    yield return new SearchHit(relativePath, i + 1, lines[i]);
                }
            }
        }
    }

    private static bool MatchesSimpleGlob(string filename, string pattern)
    {
        var regexPattern = "^" + pattern
            .Replace(".", "\\.")
            .Replace("*", ".*")
            .Replace("?", ".")
            + "$";

        try
        {
            return Regex.IsMatch(filename, regexPattern, RegexOptions.IgnoreCase);
        }
        catch
        {
            return true;
        }
    }
}
