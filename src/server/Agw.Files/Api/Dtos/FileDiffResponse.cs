namespace Agw.Files.Api.Dtos;

/// <summary>
/// 文件在 diff 范围内的改动：unified diff 文字与两侧全文。
/// A file's change in a diff scope: the unified diff text and the full text of both sides.
/// </summary>
public sealed class FileDiffResponse
{
    /// <summary>git 输出的 unified diff。The unified diff from git.</summary>
    public required string Diff { get; init; }

    /// <summary>git 没有报告改动。Git reports no change.</summary>
    public required bool Unchanged { get; init; }

    /// <summary>任一侧是二进制文件；此时两侧内容都为 null。Either side is binary; both contents are then null.</summary>
    public required bool IsBinary { get; init; }

    /// <summary>
    /// 原始侧的全文（staged、缺省范围为 HEAD，unstaged 为暂存区）；该侧没有这个文件时为 null。
    /// The original side's text (HEAD for staged and the default scope, the index for unstaged); null when absent.
    /// </summary>
    public required string? OriginalContent { get; init; }

    /// <summary>
    /// 修改侧的全文（staged 为暂存区，其他范围为工作区）；该侧没有这个文件时为 null。
    /// The modified side's text (the index for staged, the working tree otherwise); null when absent.
    /// </summary>
    public required string? ModifiedContent { get; init; }
}
