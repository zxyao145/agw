using System.Text.Json;

namespace DSystem.Shared.Tasks.Entities;

public class TaskRecord
{
    public Guid Id { get; set; }

    /// <summary>
    /// Used to associate multiple TaskRecords, equivalent to traceId
    /// </summary>
    public string ContextId { get; set; } = string.Empty;

    /// <summary>
    /// current TaskRecord's sessionId
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
