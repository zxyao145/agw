using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Data.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agents;

[Table("agent_mcp_server_relation")]
[EntityTypeConfiguration(typeof(AgentMcpServerRelationConfiguration))]
public class AgentMcpServerRelation : IAggregateRoot
{
    public Guid AgentId { get; set; }
    public Guid McpToolServerId { get; set; }

    public Agent Agent { get; set; } = null!;
    public McpServer McpToolServer { get; set; } = null!;
}
