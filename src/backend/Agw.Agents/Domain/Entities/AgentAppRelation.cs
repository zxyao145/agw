using System.ComponentModel.DataAnnotations.Schema;

using Agw.Integrations.Domain.Entities;
using Agw.Shared.Data;

namespace Agw.Agents.Domain.Entities;

[Table("agent_app_relation")]
public class AgentAppRelation : IAggregateRoot
{
    public Guid AgentId { get; set; }
    public Guid AppInstanceId { get; set; }

    public Agent Agent { get; set; } = null!;
    public AppInstance AppInstance { get; set; } = null!;
}
