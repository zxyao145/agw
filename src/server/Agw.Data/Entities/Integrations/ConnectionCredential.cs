using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Integrations;

/// <summary>
/// Represents a protected credential owned by an integration connection.
/// </summary>
[Table("integration_connection_credential")]
[EntityTypeConfiguration(typeof(ConnectionCredentialConfiguration))]
public class ConnectionCredential : BaseEntity
{
    public Guid Id { get; set; }
    public Guid ConnectionId { get; set; }
    public string Slot { get; set; } = string.Empty;
    [JsonIgnore]
    public string ProtectedValue { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    [JsonIgnore]
    public string? MetadataJson { get; set; }
    public int FormatVersion { get; set; } = 1;
    [JsonIgnore]
    public Connection Connection { get; set; } = null!;
}
