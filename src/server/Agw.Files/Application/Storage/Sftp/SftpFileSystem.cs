using System.Text.RegularExpressions;

using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Files.Exceptions;

using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Agw.Files.Application.Storage.Sftp;

public sealed class SftpFileSystem : IAgwFileSystem, IAsyncDisposable
{
    private readonly SftpClient _client;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly string _rootPath;
    private bool _disposed;

    public SftpFileSystem(SftpClient client, string rootPath)
    {
        _client = client ?? throw new AgwFilesException(
            FilesErrorCode.InvalidParameter,
            "SFTP client is required.");
        _rootPath = NormalizePath(rootPath);
    }

    private static string NormalizePath(string path)
    {
        return "/" + path.TrimStart('/').TrimEnd('/');
    }

    private string ResolvePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(normalized))
        {
            return _rootPath;
        }

        return $"{_rootPath}/{normalized}";
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (!_client.IsConnected)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                if (!_client.IsConnected)
                {
                    await _client.ConnectAsync(ct);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);
            return await operation();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task ExecuteAsync(Func<Task> operation, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);
            await operation();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> ExistsFileAsync(string path, CancellationToken ct)
    {
        var remotePath = ResolvePath(path);
        return await ExecuteAsync(async () => await _client.ExistsAsync(remotePath, ct), ct);
    }

    public async Task<bool> ExistsDirectoryAsync(string path, CancellationToken ct)
    {
        var remotePath = ResolvePath(path);
        return await ExecuteAsync(async () => await _client.ExistsAsync(remotePath, ct), ct);
    }

    public async Task<FileEntry?> StatAsync(string path, CancellationToken ct)
    {
        var remotePath = ResolvePath(path);
        return await ExecuteAsync(async () =>
        {
            if (!await _client.ExistsAsync(remotePath, ct))
            {
                return null;
            }

            var stat = await Task.Run(() =>
                    _client.ListDirectory(remotePath)
                        .FirstOrDefault(f =>
                            f.FullName.TrimEnd('/') == remotePath.TrimEnd('/')
                        )
                , ct);

            if (stat == null)
            {
                return new FileEntry(Path: path, IsDirectory: false, Size: 0, LastModifiedUtc: TimeProvider.System.GetUtcNow());
            }

            return new FileEntry(
                Path: path,
                IsDirectory: stat.IsDirectory,
                Size: stat.Length,
                LastModifiedUtc: stat.LastWriteTimeUtc);
        }, ct);
    }

    public async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
    {
        var remotePath = ResolvePath(path);
        return await ExecuteAsync(async () =>
        {
            using var stream = new MemoryStream();
            await Task.Run(() => _client.DownloadFile(remotePath, stream), ct);
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct);
        }, ct);
    }

    public async Task<string[]> ReadAllLinesAsync(string path, CancellationToken ct)
    {
        var remotePath = ResolvePath(path);
        return await ExecuteAsync(async () =>
        {
            using var stream = new MemoryStream();
            await Task.Run(() => _client.DownloadFile(remotePath, stream), ct);
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(ct);
            return content.Split('\n');
        }, ct);
    }

    public async Task WriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        var remotePath = ResolvePath(path);
        await ExecuteAsync(async () =>
        {
            using var stream = new MemoryStream();
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(content.AsMemory(), ct);
            await writer.FlushAsync(ct);
            stream.Position = 0;
            await Task.Run(() => _client.UploadFile(stream, remotePath), ct);
        }, ct);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        var remotePath = ResolvePath(path);
        await ExecuteAsync(async () => { await _client.CreateDirectoryAsync(remotePath, ct); }, ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        var remotePath = ResolvePath(path);
        await ExecuteAsync(async () =>
        {
            if (await _client.ExistsAsync(remotePath, ct))
            {
                var isDir = await Task.Run(() =>
                {
                    var attrs = _client.GetAttributes(remotePath);
                    return attrs.IsDirectory;
                }, ct);

                if (isDir)
                {
                    await RecursiveDeleteAsync(remotePath, ct);
                }
                else
                {
                    await _client.DeleteFileAsync(remotePath, ct);
                }
            }
        }, ct);
    }

    private async Task RecursiveDeleteAsync(string remotePath, CancellationToken ct = default)
    {
        await foreach (ISftpFile entry in _client.ListDirectoryAsync(remotePath, ct))
        {
            string? name = entry.Name;
            if (name is "." or "..") continue;

            if (entry.IsDirectory)
            {
                await RecursiveDeleteAsync(entry.FullName);
            }
            else
            {
                await _client.DeleteFileAsync(entry.FullName, ct);
            }
        }

        await _client.DeleteDirectoryAsync(remotePath, ct);
    }

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(
        string path,
        string searchPattern,
        bool recursive,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct)
    {
        var remotePath = ResolvePath(path);

        await _semaphore.WaitAsync(ct);
        List<ISftpFile> files;
        try
        {
            await EnsureConnectedAsync(ct);
            files = [.. _client.ListDirectory(remotePath)];
        }
        finally
        {
            _semaphore.Release();
        }

        var simplePattern = searchPattern.Replace("*", "");

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var name = file.Name;
            if (name is "." or "..") continue;

            // Simple glob matching for searchPattern
            if (!MatchesSftpGlob(name, searchPattern)) continue;

            var relativePath = path.TrimEnd('/') + "/" + name;

            yield return new FileEntry(
                Path: relativePath,
                IsDirectory: file.IsDirectory,
                Size: file.Length,
                LastModifiedUtc: file.LastWriteTimeUtc);

            if (recursive && file.IsDirectory)
            {
                await foreach (var subEntry in EnumerateAsync(relativePath, searchPattern, true, ct))
                {
                    yield return subEntry;
                }
            }
        }
    }

    private static bool MatchesSftpGlob(string name, string pattern)
    {
        if (pattern == "*") return true;

        var regexStr = "^" + pattern
                               .Replace(".", "\\.")
                               .Replace("*", ".*")
                               .Replace("?", ".")
                           + "$";
        return Regex.IsMatch(name, regexStr, RegexOptions.IgnoreCase);
    }

    public async IAsyncEnumerable<SearchHit> SearchAsync(
        string rootPath,
        SearchOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct)
    {
        var remotePath = ResolvePath(rootPath);
        var files = new List<string>();

        await _semaphore.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);
            CollectSftpFiles(remotePath, files);
        }
        finally
        {
            _semaphore.Release();
        }

        var regexOptions = RegexOptions.Compiled;
        if (options.CaseInsensitive) regexOptions |= RegexOptions.IgnoreCase;
        if (options.Multiline) regexOptions |= RegexOptions.Singleline;

        Regex regex;
        try
        {
            regex = new Regex(options.Pattern, regexOptions);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        var hitCount = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (options.MaxHits.HasValue && hitCount >= options.MaxHits.Value) yield break;

            if (options.IncludeExtensions is { Count: > 0 })
            {
                var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
                if (!options.IncludeExtensions.Contains(ext)) continue;
            }

            if (!string.IsNullOrEmpty(options.FilenameGlob))
            {
                var filename = Path.GetFileName(file);
                if (!MatchesSftpGlob(filename, options.FilenameGlob)) continue;
            }

            string content;
            await _semaphore.WaitAsync(ct);
            try
            {
                await EnsureConnectedAsync(ct);
                using var stream = new MemoryStream();
                await Task.Run(() => _client.DownloadFile(file, stream), ct);
                stream.Position = 0;
                using var reader = new StreamReader(stream);
                content = await reader.ReadToEndAsync(ct);
            }
            catch
            {
                continue;
            }
            finally
            {
                _semaphore.Release();
            }

            var lines = content.Split('\n');
            var relativePath = file[(_rootPath.Length + 1)..];

            for (int i = 0; i < lines.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (options.MaxHits.HasValue && hitCount >= options.MaxHits.Value) yield break;

                if (regex.IsMatch(lines[i]))
                {
                    hitCount++;
                    yield return new SearchHit(relativePath, i + 1, lines[i]);
                }
            }
        }
    }

    private void CollectSftpFiles(string remotePath, List<string> files)
    {
        foreach (var entry in _client.ListDirectory(remotePath))
        {
            var name = entry.Name;
            if (name is "." or "..") continue;

            if (entry.IsDirectory)
            {
                CollectSftpFiles(entry.FullName, files);
            }
            else
            {
                files.Add(entry.FullName);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _semaphore.Dispose();
        await Task.Run(() =>
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }

            _client.Dispose();
        });
        GC.SuppressFinalize(this);
    }
}
