using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace Agw.Tools.Impl.ToolBlocks.Todo;

/// <summary>
/// Represents the state of the todo list managed by the <see cref="AgwTodoProvider"/>,
/// stored in the session's <see cref="AgentSessionStateBag"/>.
/// </summary>
internal sealed class AgwTodoState
{
    /// <summary>
    /// Gets the list of todo items.
    /// </summary>
    [JsonPropertyName("items")]
    public List<AgwTodoItem> Items { get; set; } = [];

    /// <summary>
    /// Gets or sets the next ID to assign to a new todo item.
    /// </summary>
    [JsonPropertyName("nextId")]
    public int NextId { get; set; } = 1;
}
