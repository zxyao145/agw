using System.Text.Json.Serialization;

namespace Agw.Files.Abstracts.Dtos;

/// <summary>
/// Represents a matching line found during a content search.
/// 表示内容搜索时命中的一行。
/// </summary>
public sealed class AgwFileSearchMatch
{
    /// <summary>
    /// Gets or sets the 1-based line number, numbered the way <see cref="TextContentEditor"/> splits lines.
    /// 从 1 开始的行号，按 <see cref="TextContentEditor"/> 的切分规则编号。
    /// </summary>
    [JsonPropertyName("lineNumber")]
    public int LineNumber { get; set; }

    /// <summary>
    /// Gets or sets the matching line.
    /// 命中的行内容。
    /// </summary>
    [JsonPropertyName("line")]
    public string Line { get; set; } = string.Empty;
}
