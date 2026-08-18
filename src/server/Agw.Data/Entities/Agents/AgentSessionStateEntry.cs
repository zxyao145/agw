using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agents;

[Table("agent_session_state")]
[EntityTypeConfiguration(typeof(AgentSessionStateEntryConfiguration))]
public sealed class AgentSessionStateEntry
{
    public Guid ProjectConversationId { get; set; }

    public Guid AgentId { get; set; }

    public string AgentflowNodeId { get; set; } = string.Empty;

    public string SerializedSession { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public ProjectConversation? ProjectConversation { get; set; }

    [JsonIgnore]
    public Agent? Agent { get; set; }
}
