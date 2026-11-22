using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agents;

[Table("agent_a2a_endpoint")]
[EntityTypeConfiguration(typeof(AgentA2AEndpointConfiguration))]
public sealed class AgentA2AEndpoint
{
    public Guid AgentId { get; set; }
    public string CardUrl { get; set; } = string.Empty;

    [Encrypted]
    [JsonIgnore]
    public Dictionary<string, string> Credentials { get; set; } = [];

    public string InterfaceUrl { get; set; } = string.Empty;
    public string ProtocolBinding { get; set; } = string.Empty;
    public Guid InterfaceRevision { get; set; }
    public bool Streaming { get; set; }
    public required AgentA2AEndpointExtra Extra { get; set; }

    [JsonIgnore]
    public Agent? Agent { get; set; }
}
