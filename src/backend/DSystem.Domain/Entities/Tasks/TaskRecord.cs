using DSystem.Shared;
using DSystem.Shared.Models;
using System.Text.Json;

namespace DSystem.Domain.Entities;

public class TaskRecord : BaseEntity
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

    /// <summary>
    /// User input to be executed by the associated target.
    /// </summary>
    public required UserInputMessage Input { get; set; }

    /// <summary>
    /// agent 的处理
    /// </summary>
    public List<AiMessage> Messages { get; set; } = [];

    public Dictionary<string, JsonElement> Metadata { get; set; } = [];

    public string? Error { get; set; }
}
