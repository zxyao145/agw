using System.Text.Json.Serialization;

namespace Agw.Files.Abstracts.Dtos;

/// <summary>
/// Represents a single direct child of a listed directory, as returned by the <c>ls</c> tools.
/// 表示所列目录的一个直接子项，是 <c>ls</c> 工具的返回结构。
/// </summary>
public sealed class AgwFileStoreEntry
{
    /// <summary>The <see cref="Type"/> value for a regular file.</summary>
    public const string File = "file";

    /// <summary>The <see cref="Type"/> value for a subdirectory.</summary>
    public const string Directory = "directory";

    public AgwFileStoreEntry() { }

    public AgwFileStoreEntry(string name, string type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>
    /// Gets or sets the entry name, a single path segment relative to the listed directory.
    /// 条目名称，是相对于所列目录的单个路径段。
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the entry type, either <see cref="File"/> or <see cref="Directory"/>.
    /// 条目类型，取值为 <see cref="File"/> 或 <see cref="Directory"/>。
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = File;
}
