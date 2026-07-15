namespace Agw.Files.Abstracts.Dtos;

public sealed record FileEntry(
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastModifiedUtc);
