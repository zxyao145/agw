using System.Text.Json;

namespace Agw.Shared.Tasks.Entities;

public class TaskRecord
{
    public Guid Id { get; set; }

    /// <summary>
    /// TaskRecord session id, unified as ProjectTask.Id
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    public string? AgentName { get; set; }

    public long? ConversationSequence { get; set; }

    public string? ConversationPayload { get; set; }

    ///// <summary>
    ///// User input to be executed by the associated target.
    ///// </summary>
    //public UserInputMessage? Input { get; set; }

    public Dictionary<string, JsonElement>? Metadata { get; set; }

    public string? Error { get; set; }

    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
}
