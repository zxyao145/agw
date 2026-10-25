namespace Agw.Files.Application.Files;

public sealed record FileListEntry(
    string Name,
    string Path,
    string Type,
    long? Size,
    DateTimeOffset? ModifiedTime,
    string? GitStatus,
    string? GitStagedStatus,
    string? GitUnstagedStatus
);

public sealed record FileListOutput(IReadOnlyList<FileListEntry> Items);

public sealed record FileSearchEntry(string FullPath, string RelativePath, string Type);

public sealed record FileSearchOutput(IReadOnlyList<FileSearchEntry> Results);

/// <summary>
/// 一个文件在 diff 范围内的改动与两侧全文。The change of a file in a diff scope, with the full text of both sides.
/// </summary>
public sealed class FileDiffOutput
{
    public required string Diff { get; init; }

    /// <summary>git 没有报告改动。Git reports no change.</summary>
    public required bool Unchanged { get; init; }

    /// <summary>任一侧是二进制文件；此时两侧内容都为 null。Either side is binary; both contents are then null.</summary>
    public required bool IsBinary { get; init; }

    /// <summary>原始侧的全文；该侧没有这个文件时为 null。The original side's text; null when the file is absent there.</summary>
    public required string? OriginalContent { get; init; }

    /// <summary>修改侧的全文；该侧没有这个文件时为 null。The modified side's text; null when the file is absent there.</summary>
    public required string? ModifiedContent { get; init; }
}

public sealed record FileMutationOutput(bool Success, string Message);
