using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Data.Entities.Integrations;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agents;

[Table("agent_app_relation")]
[EntityTypeConfiguration(typeof(AgentAppRelationConfiguration))]
public class AgentAppRelation : IAggregateRoot
{
    public Guid AgentId { get; set; }
    public Guid AppInstanceId { get; set; }

    public Agent Agent { get; set; } = null!;
    public AppInstance AppInstance { get; set; } = null!;
}
