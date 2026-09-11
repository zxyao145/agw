using System.Text.Json.Serialization;

namespace Agw.Tools.Impl.ToolBlocks.Todo;

/// <summary>
/// Represents a single todo item managed by the <see cref="AgwTodoProvider"/>.
/// </summary>
public sealed class AgwTodoItem
{
    /// <summary>
    /// Gets or sets the unique identifier for this todo item.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the title of this todo item.
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional description providing additional details about this todo item.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this todo item has been completed.
    /// </summary>
    [JsonPropertyName("isComplete")]
    public bool IsComplete { get; set; }
}
