namespace DSystem.Domain.Entities;

public class AgentMcpToolServer
{
    public Guid AgentId { get; set; }
    public Guid McpToolServerId { get; set; }

    public Agent Agent { get; set; } = null!;
    public McpToolServer McpToolServer { get; set; } = null!;
}
