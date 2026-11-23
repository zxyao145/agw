using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agentflows;

[Table("agentflow_revision")]
[EntityTypeConfiguration(typeof(AgentflowRevisionConfiguration))]
public sealed class AgentflowRevision : BaseEntity
{
    public Guid AgentflowId { get; set; }

    public int Revision { get; set; }
}
