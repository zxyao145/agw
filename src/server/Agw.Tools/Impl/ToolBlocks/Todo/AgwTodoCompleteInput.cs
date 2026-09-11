using System.Text.Json.Serialization;

namespace Agw.Tools.Impl.ToolBlocks.Todo;

/// <summary>
/// Represents the input for completing a single todo item via the <see cref="AgwTodoProvider"/>.
/// </summary>
internal sealed class AgwTodoCompleteInput
{
    /// <summary>
    /// Gets or sets the ID of the todo item to mark as complete.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the reason describing how or why the item was completed.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}
