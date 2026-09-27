using System.Text.Json.Serialization;

namespace Agw.Tools.Impl.ToolBlocks.Storage;

/// <summary>
/// Represents a matching line found during a search.
/// 表示搜索时命中的一行。
/// </summary>
public sealed class AgwFileSearchMatch
{
    /// <summary>
    /// Gets or sets the 1-based line number, addressing the same lines the line-edit tools use.
    /// 从 1 开始的行号，与按行编辑工具使用的行一致。
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
