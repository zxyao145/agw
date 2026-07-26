using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Integrations;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agents;

[Table("agent_connection_relation")]
[EntityTypeConfiguration(typeof(AgentConnectionRelationConfiguration))]
public class AgentConnectionRelation : IAggregateRoot
{
    public Guid AgentId { get; set; }
    public Guid ConnectionId { get; set; }
    [JsonIgnore]
    public Agent Agent { get; set; } = null!;
    [JsonIgnore]
    public Connection Connection { get; set; } = null!;
}
