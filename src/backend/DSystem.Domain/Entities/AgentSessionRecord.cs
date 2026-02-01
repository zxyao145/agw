namespace DSystem.Domain.Entities;

public class AgentSessionRecord : BaseEntity
{
    public long Id { get; set; }
    public Guid? ProjectId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string Messages { get; set; } = string.Empty;
}
