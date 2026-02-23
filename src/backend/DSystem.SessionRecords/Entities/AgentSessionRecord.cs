using DSystem.Shared;

namespace DSystem.SessionRecords.Entities;

public class AgentSessionRecord : BaseEntity
{
    public long Id { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Messages { get; set; } = string.Empty;
}
