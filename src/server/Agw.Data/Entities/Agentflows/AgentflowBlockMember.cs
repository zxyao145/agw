using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agentflows;

[Table("agentflow_block_member")]
[EntityTypeConfiguration(typeof(AgentflowBlockMemberConfiguration))]
public sealed class AgentflowBlockMember : BaseEntity
{
    public Guid AgentflowId { get; set; }

    public int AgentflowRevision { get; set; }

    public string NodeId { get; set; } = string.Empty;

    public string MemberId { get; set; } = string.Empty;

    public AgentflowNodeKind Kind { get; set; }

    public Guid RelateId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Instructions { get; set; }

    public int SortOrder { get; set; }

    public bool IsManager { get; set; }
}
