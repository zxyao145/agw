using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Data.Entities.Agents;

namespace Agw.Shared.Data.Entities.Tasks;

[Table("project_mcp_server_relation")]
public class ProjectMcpServerRelation : IAggregateRoot
{
    public Guid ProjectId { get; set; }
    public Guid McpToolServerId { get; set; }

    public Project Project { get; set; } = null!;
    public McpServer McpToolServer { get; set; } = null!;
}
