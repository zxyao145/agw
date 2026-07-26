using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Agw.Shared.Data.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Integrations;

/// <summary>
/// Represents an external account or service endpoint that agents and projects can use.
/// </summary>
[Table("integration_connection")]
[EntityTypeConfiguration(typeof(ConnectionConfiguration))]
public class Connection : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }
    public string PluginId { get; set; } = string.Empty;
    public string ConnectorId { get; set; } = string.Empty;
    public string AuthSchemeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string ConfigurationJson { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
    public ConnectionStatus Status { get; set; } = ConnectionStatus.NeedsConfiguration;
    public string? Subject { get; set; }
    public DateTimeOffset? LastValidatedAtUtc { get; set; }
    public string? LastValidationErrorCode { get; set; }
    public string? ValidationMetadataJson { get; set; }
    [JsonIgnore]
    public ICollection<ConnectionCredential> Credentials { get; set; } = new List<ConnectionCredential>();
}
