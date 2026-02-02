namespace DSystem.Domain.Entities;

public class AgentSessionRecord : BaseEntity
{
    public long Id { get; set; }
    public Guid ProjectId { get; set; } = Guid.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Messages { get; set; } = string.Empty;
}
