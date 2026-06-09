namespace Agw.Shared.Contracts.Storage;

public sealed record FileEntry(
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastModifiedUtc);
