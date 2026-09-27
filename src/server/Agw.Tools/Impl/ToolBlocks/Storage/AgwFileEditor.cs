using Agw.Shared.Exceptions;

namespace Agw.Tools.Impl.ToolBlocks.Storage;

/// <summary>
/// Text editing helpers for the <c>replace</c>, <c>replace_lines</c>, and <c>read_lines</c> file tools.
/// 供 <c>replace</c>、<c>replace_lines</c> 和 <c>read_lines</c> 文件工具使用的文本编辑辅助方法。
/// </summary>
/// <remarks>
/// A line ends with <c>\n</c>, <c>\r\n</c>, or a lone <c>\r</c> and keeps its terminator; content that ends with a
/// terminator has no extra empty line after it. Failure messages reach the model, so they name the tool arguments.
/// 行以 <c>\n</c>、<c>\r\n</c> 或单独的 <c>\r</c> 结束并保留结束符；以结束符结尾的内容之后不再有额外空行。
/// 失败消息会返回给模型，因此消息中使用工具参数名。
/// </remarks>
internal static class AgwFileEditor
{
    /// <summary>
    /// Replaces occurrences of <paramref name="oldString"/> and returns the new content with the replacement count.
    /// 替换 <paramref name="oldString"/> 的出现位置，返回新内容和替换次数。
    /// </summary>
    internal static (string Content, int Count) ApplyReplace(
        string content,
        string oldString,
        string newString,
        bool replaceAll
    )
    {
        if (string.IsNullOrEmpty(oldString))
        {
            throw InvalidParameter("oldString must not be empty.");
        }

        var count = CountOccurrences(content, oldString);
        if (count == 0)
        {
            throw InvalidParameter($"oldString not found: '{oldString}'.");
        }

        if (count > 1 && !replaceAll)
        {
            throw InvalidParameter(
                $"oldString occurs {count} times; pass replaceAll=true to replace all, "
                    + "or provide a more specific oldString."
            );
        }

        return (content.Replace(oldString, newString, StringComparison.Ordinal), count);
    }

    /// <summary>
    /// Applies literal 1-based line replacements; an empty <see cref="AgwFileLineEdit.NewLine"/> deletes the line
    /// together with its line break.
    /// 按从 1 开始的行号原样替换整行；<see cref="AgwFileLineEdit.NewLine"/> 为空时连同换行符一起删除该行。
    /// </summary>
    /// <remarks>
    /// All edits are validated before any is applied, so a rejected batch leaves the content unchanged.
    /// 所有编辑先全部校验再统一应用，任何一项被拒绝时内容保持不变。
    /// </remarks>
    internal static string ApplyReplaceLines(string content, IReadOnlyList<AgwFileLineEdit> edits)
    {
        if (edits.Count == 0)
        {
            throw InvalidParameter("At least one line edit must be provided.");
        }

        var lines = SplitLinesKeepEnds(content);
        var seen = new HashSet<int>();
        foreach (var edit in edits)
        {
            if (!seen.Add(edit.LineNumber))
            {
                throw InvalidParameter($"Duplicate line_number {edit.LineNumber} in edits.");
            }

            if (edit.LineNumber < 1 || edit.LineNumber > lines.Count)
            {
                throw InvalidParameter(
                    $"line_number {edit.LineNumber} is out of range (file has {lines.Count} lines)."
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
                throw InvalidParameter(
                    $"line_number {edit.LineNumber} does not match the expected text. "
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
    /// Returns the 1-based inclusive <c>[startLine, endLine]</c> slice with each line's terminator attached. An
    /// <paramref name="endLine"/> past the last line is clamped, and omitting it reads to the end of the content.
    /// 返回从 1 开始、首尾都包含的 <c>[startLine, endLine]</c> 行区间，每行保留结束符；
    /// <paramref name="endLine"/> 超过最后一行时截取到最后一行，省略时读到内容末尾。
    /// </summary>
    internal static List<string> SliceLines(string content, int startLine, int? endLine)
    {
        var lines = SplitLinesKeepEnds(content);
        var total = lines.Count;

        if (startLine < 1)
        {
            throw InvalidParameter($"startLine must be a positive integer, got {startLine}.");
        }

        if (endLine is < 1)
        {
            throw InvalidParameter($"endLine must be a positive integer, got {endLine}.");
        }

        if (endLine < startLine)
        {
            throw InvalidParameter($"endLine ({endLine}) must not be less than startLine ({startLine}).");
        }

        if (startLine > total)
        {
            throw InvalidParameter($"startLine {startLine} is out of range (file has {total} lines).");
        }

        var lastLine = endLine == null ? total : Math.Min(endLine.Value, total);
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

    private static AgwException InvalidParameter(string message) => new(ErrorCodes.InvalidParam, message);
}
