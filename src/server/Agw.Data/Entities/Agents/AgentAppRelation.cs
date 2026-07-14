using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Data.Entities.Integrations;

namespace Agw.Shared.Data.Entities.Agents;

[Table("agent_app_relation")]
public class AgentAppRelation : IAggregateRoot
{
    public Guid AgentId { get; set; }
    public Guid AppInstanceId { get; set; }

    public Agent Agent { get; set; } = null!;
    public AppInstance AppInstance { get; set; } = null!;
}
