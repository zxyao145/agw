namespace Agw.Shared.Contracts.Storage;

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

public interface IAgwFileSystemResolver
{
    Task<IAgwFileSystem> ResolveAsync(Guid projectId, CancellationToken ct);
}
