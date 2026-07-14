using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Shared.Data.Entities.Agents;

[Table("agent_mcp_server_relation")]
public class AgentMcpServerRelation : IAggregateRoot
{
    public Guid AgentId { get; set; }
    public Guid McpToolServerId { get; set; }

    public Agent Agent { get; set; } = null!;
    public McpServer McpToolServer { get; set; } = null!;
}
