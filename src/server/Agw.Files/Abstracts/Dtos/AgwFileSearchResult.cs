using System.Text.Json.Serialization;

namespace Agw.Files.Abstracts.Dtos;

/// <summary>
/// Represents a file that matched a content search, with a snippet and the matching lines.
/// 表示内容搜索命中的一个文件，包含片段和命中的行。
/// </summary>
public sealed class AgwFileSearchResult
{
    /// <summary>
    /// Gets or sets the path of the matching file, relative to the root of the searched file system or store.
    /// 命中文件相对于所搜索文件系统或 store 根目录的路径。
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the first matching line.
    /// 第一条命中的行。
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
