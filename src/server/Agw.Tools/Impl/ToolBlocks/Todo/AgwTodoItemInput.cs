using System.Text.Json.Serialization;

namespace Agw.Tools.Impl.ToolBlocks.Todo;

/// <summary>
/// Represents the input for creating a new todo item via the <see cref="AgwTodoProvider"/>.
/// </summary>
internal sealed class AgwTodoItemInput
{
    /// <summary>
    /// Gets or sets the title of the todo item to create.
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional description providing additional details about the todo item.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
