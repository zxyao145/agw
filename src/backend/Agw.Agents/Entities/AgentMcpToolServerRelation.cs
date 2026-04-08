using Agw.Shared;
using Agw.Shared.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Domain.Entities;

[Table("agent_mcp_server_relation")]
public class AgentMcpToolServerRelation : IAggregateRoot
{
    public Guid AgentId { get; set; }
    public Guid McpToolServerId { get; set; }

    public Agent Agent { get; set; } = null!;
    public McpToolServer McpToolServer { get; set; } = null!;
}
