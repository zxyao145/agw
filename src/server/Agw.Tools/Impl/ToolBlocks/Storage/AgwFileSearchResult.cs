using System.Text.Json.Serialization;

namespace Agw.Tools.Impl.ToolBlocks.Storage;

/// <summary>
/// Represents a file that matched a search, with a content snippet and the matching lines.
/// 表示搜索命中的文件，包含内容片段和命中的行。
/// </summary>
public sealed class AgwFileSearchResult
{
    /// <summary>
    /// Gets or sets the path of the matching file.
    /// 命中文件的路径。
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a snippet of content around the first match.
    /// 第一个命中位置附近的内容片段。
    /// </summary>
    [JsonPropertyName("snippet")]
    public string Snippet { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the lines where matches were found.
    /// 命中的行。
    /// </summary>
    [JsonPropertyName("matchingLines")]
    public List<AgwFileSearchMatch> MatchingLines { get; set; } = [];
}
