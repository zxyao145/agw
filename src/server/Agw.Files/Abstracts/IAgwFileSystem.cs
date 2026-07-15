using Agw.Files.Abstracts.Dtos;

namespace Agw.Files.Abstracts;

/// <summary>
/// 对文件系统的抽象。实现应该持有一个根目录，所有的操作都是在根目录下进行，同时注意防止路径逃逸。
/// </summary>
public interface IAgwFileSystem
{
    Task<bool> ExistsFileAsync(string path, CancellationToken ct);

    Task<bool> ExistsDirectoryAsync(string path, CancellationToken ct);

    Task<FileEntry?> StatAsync(string path, CancellationToken ct);
    Task<string> ReadAllTextAsync(string path, CancellationToken ct);
    Task<string[]> ReadAllLinesAsync(string path, CancellationToken ct);
    Task WriteAllTextAsync(string path, string content, CancellationToken ct);
    Task CreateDirectoryAsync(string path, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
    IAsyncEnumerable<FileEntry> EnumerateAsync(
        string path,
        string searchPattern,
        bool recursive,
        CancellationToken ct);

    IAsyncEnumerable<SearchHit> SearchAsync(
        string rootPath,
        SearchOptions options,
        CancellationToken ct);
}
