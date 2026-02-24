using DSystem.Shared;
using DSystem.Shared.Models;
using System.Text.Json;

namespace DSystem.SessionRecords.Entities;

public class AgentSessionRecord : BaseEntity
{
    public long Id { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string Role { get; set; } = string.Empty;
    public Dictionary<string, JsonElement>? Metadata { get; set; }
    public List<AiMessageContent> Contents { get; set; } = [];
    public string? Error { get; set; }

}
