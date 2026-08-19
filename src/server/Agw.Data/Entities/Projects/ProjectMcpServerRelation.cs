using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("project_mcp_server_relation")]
[EntityTypeConfiguration(typeof(ProjectMcpServerRelationConfiguration))]
public class ProjectMcpServerRelation : IAggregateRoot
{
    public Guid ProjectId { get; set; }
    public Guid McpToolServerId { get; set; }

    public Project Project { get; set; } = null!;
    public McpServer McpToolServer { get; set; } = null!;
}
