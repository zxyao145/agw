using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Agw.Files.Abstracts.Dtos;

/// <summary>
/// Represents a single whole-line replacement used by <see cref="IAgwFileSystem.ReplaceLinesAsync"/> and the
/// <c>replace_lines</c> tools, whose parameter schema is generated from this type.
/// 表示 <see cref="IAgwFileSystem.ReplaceLinesAsync"/> 与 <c>replace_lines</c> 工具使用的单行整体替换，工具的参数 Schema 由本类型生成。
/// </summary>
public sealed class AgwFileLineEdit
{
    /// <summary>
    /// Gets or sets the 1-based line number to replace.
    /// 要替换的行号，从 1 开始。
    /// </summary>
    [JsonPropertyName("line_number")]
    [Description("1-based line number to replace.")]
    public int LineNumber { get; set; }

    /// <summary>
    /// Gets or sets the literal replacement text, including any trailing newline to keep; an empty string deletes the
    /// line together with its line break.
    /// 替换后的原样文本，需要保留的行尾换行符由调用方自行包含；空字符串表示连同换行符一起删除该行。
    /// </summary>
    [JsonPropertyName("new_line")]
    [Description(
        "Literal replacement text for the line, including any trailing newline you want to keep (the editor does not add one). Set to an empty string to delete the line entirely, including its line break."
    )]
    public string NewLine { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text the caller believes is on the line. When set, the edit is rejected unless it matches,
    /// ignoring the line terminator, which catches a stale line number or a file that changed since it was read.
    /// 调用方认为该行当前的文本；设置后必须与实际内容一致（忽略行结束符）才会执行编辑，用于发现过期的行号或读取后被修改的文件。
    /// </summary>
    [JsonPropertyName("expected_line")]
    [Description(
        "Optional: the text you believe is currently on that line, as reported by grep. Give the line's own text only: a numbered read prefixes each line with its number and a tab, and that prefix is not part of the line. When supplied, the edit is rejected unless it matches, which catches an out-of-date line number or a file that changed since you looked. The trailing newline is ignored in the comparison."
    )]
    public string? ExpectedLine { get; set; }
}
