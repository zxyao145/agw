using Agw.Files.Abstracts.Dtos;
using Agw.Shared.Exceptions;

namespace Agw.Files.Abstracts;

/// <summary>
/// 文本内容的替换、按行编辑与按行读取算法；<see cref="IAgwFileSystem"/> 的实现和非文件存储都使用它，保证行号语义一致。
/// Text replacement, line editing, and line slicing shared by <see cref="IAgwFileSystem"/> implementations and
/// non-file stores so line numbers mean the same everywhere.
/// </summary>
/// <remarks>
/// 行以 <c>\n</c>、<c>\r\n</c> 或单独的 <c>\r</c> 结束并保留结束符，与 <c>StreamReader.ReadLine</c> 的切分规则一致；
/// 以结束符结尾的内容之后不再有额外空行。
/// A line ends with <c>\n</c>, <c>\r\n</c>, or a lone <c>\r</c> and keeps its terminator, matching
/// <c>StreamReader.ReadLine</c>; content that ends with a terminator has no extra empty line after it.
/// </remarks>
public static class TextContentEditor
{
    /// <summary>
    /// 替换 <paramref name="oldString"/> 的出现位置，返回新内容和替换次数。
    /// Replaces occurrences of <paramref name="oldString"/> and returns the new content with the replacement count.
    /// </summary>
    public static (string Content, int Count) ApplyReplace(
        string content,
        string oldString,
        string newString,
        bool replaceAll
    )
    {
        if (string.IsNullOrEmpty(oldString))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "oldString must not be empty.");
        }

        var count = CountOccurrences(content, oldString);
        if (count == 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"oldString not found: '{oldString}'.");
        }

        if (count > 1 && !replaceAll)
        {
            throw new AgwException(
                ErrorCodes.MultipleMatches,
                $"oldString occurs {count} times; pass replaceAll=true to replace all, "
                    + "or provide a more specific oldString."
            );
        }

        return (content.Replace(oldString, newString, StringComparison.Ordinal), count);
    }

    /// <summary>
    /// 按从 1 开始的行号整行替换；所有编辑先全部校验再统一应用，任何一项被拒绝时内容保持不变。
    /// Applies 1-based whole-line edits; every edit is validated before any is applied, so a rejected batch leaves the
    /// content unchanged.
    /// </summary>
    public static string ApplyReplaceLines(string content, IReadOnlyList<AgwFileLineEdit> edits)
    {
        if (edits.Count == 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "At least one line edit must be provided.");
        }

        var lines = SplitLinesKeepEnds(content);
        var seen = new HashSet<int>();
        foreach (var edit in edits)
        {
            if (!seen.Add(edit.LineNumber))
            {
                throw new AgwException(ErrorCodes.InvalidParam, $"Duplicate line number {edit.LineNumber} in edits.");
            }

            if (edit.LineNumber < 1 || edit.LineNumber > lines.Count)
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Line number {edit.LineNumber} is out of range (file has {lines.Count} lines)."
                );
            }

            if (
                edit.ExpectedLine != null
                && !string.Equals(
                    TrimLineTerminator(lines[edit.LineNumber - 1]),
                    TrimLineTerminator(edit.ExpectedLine),
                    StringComparison.Ordinal
                )
            )
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Line number {edit.LineNumber} does not match the expected text. "
                        + "Re-read the file to get current line numbers."
                );
            }
        }

        foreach (var edit in edits)
        {
            lines[edit.LineNumber - 1] = edit.NewLine;
        }

        return string.Concat(lines);
    }

    /// <summary>
    /// 返回从 1 开始、首尾都包含的 <c>[startLine, endLine]</c> 行区间，每行保留结束符；
    /// <paramref name="endLine"/> 超过最后一行时截取到最后一行，为 null 时读到内容末尾。
    /// Returns the 1-based inclusive <c>[startLine, endLine]</c> slice with each line's terminator attached. An
    /// <paramref name="endLine"/> past the last line is clamped, and null reads to the end of the content.
    /// </summary>
    public static IReadOnlyList<string> SliceLines(string content, int startLine, int? endLine)
    {
        if (startLine < 1)
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"startLine must be a positive integer, got {startLine}.");
        }

        if (endLine is < 1)
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"endLine must be a positive integer, got {endLine}.");
        }

        if (endLine < startLine)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"endLine ({endLine}) must not be less than startLine ({startLine})."
            );
        }

        var lines = SplitLinesKeepEnds(content);
        if (startLine > lines.Count)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"startLine {startLine} is out of range (file has {lines.Count} lines)."
            );
        }

        var lastLine = endLine == null ? lines.Count : Math.Min(endLine.Value, lines.Count);
        return lines.GetRange(startLine - 1, lastLine - startLine + 1);
    }

    private static string TrimLineTerminator(string line)
    {
        if (line.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return line[..^2];
        }

        return line.EndsWith('\n') || line.EndsWith('\r') ? line[..^1] : line;
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static List<string> SplitLinesKeepEnds(string content)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            switch (content[index])
            {
                case '\n':
                    lines.Add(content[start..(index + 1)]);
                    start = index + 1;
                    break;
                case '\r':
                    // "\r\n" 是一个结束符，单独的 "\r" 也结束一行。
                    // "\r\n" is a single terminator; a lone "\r" also ends a line.
                    var end = index + 1 < content.Length && content[index + 1] == '\n' ? index + 2 : index + 1;
                    lines.Add(content[start..end]);
                    index = end - 1;
                    start = end;
                    break;
            }
        }

        if (start < content.Length)
        {
            lines.Add(content[start..]);
        }

        return lines;
    }
}
