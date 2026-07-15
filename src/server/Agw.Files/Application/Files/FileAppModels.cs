namespace Agw.Files.Application.Files;

public sealed record FileListEntry(
    string Name,
    string Path,
    string Type,
    long? Size,
    DateTimeOffset? ModifiedTime,
    string? GitStatus);

public sealed record FileListOutput(IReadOnlyList<FileListEntry> Items);

public sealed record FileSearchEntry(
    string FullPath,
    string RelativePath,
    string Type);

public sealed record FileSearchOutput(IReadOnlyList<FileSearchEntry> Results);

public sealed record FileDiffOutput(
    string Diff,
    bool Unchanged,
    string? OriginalContent);

public sealed record FileMutationOutput(bool Success, string Message);
