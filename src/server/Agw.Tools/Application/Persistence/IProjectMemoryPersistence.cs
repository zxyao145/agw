namespace Agw.Tools.Application.Persistence;

public sealed record ProjectMemoryContentEntry(string Path, string Content);

public interface IProjectMemoryPersistence
{
    Task WriteAsync(
        Guid projectId,
        string ownerUserId,
        string path,
        string content,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default
    );

    Task<string?> ReadAsync(
        Guid projectId,
        string ownerUserId,
        string path,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteAsync(
        Guid projectId,
        string ownerUserId,
        string path,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<string>> ListPathsAsync(
        Guid projectId,
        string ownerUserId,
        string prefix,
        CancellationToken cancellationToken = default
    );

    Task<bool> FileExistsAsync(
        Guid projectId,
        string ownerUserId,
        string path,
        CancellationToken cancellationToken = default
    );

    IAsyncEnumerable<ProjectMemoryContentEntry> ListEntriesAsync(
        Guid projectId,
        string ownerUserId,
        string prefix,
        CancellationToken cancellationToken = default
    );
}
