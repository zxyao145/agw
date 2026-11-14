using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Events;

[Table("event_source")]
[EntityTypeConfiguration(typeof(EventSourceConfiguration))]
public class EventSource : BaseEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public EventSourceKind Kind { get; set; }

    public Guid? ProviderExtensionId { get; set; }

    public string? ProviderId { get; set; }

    public string? PublisherKey { get; set; }

    public string ManagedBy { get; set; } = EventManagedBy.User;

    public string? ManagedKey { get; set; }

    public string CeSource { get; set; } = string.Empty;

    public string ConfigJson { get; set; } = string.Empty;

    [JsonIgnore]
    [Encrypted]
    public string? SecretJson { get; set; }

    [JsonIgnore]
    [Encrypted]
    public string? CursorJson { get; set; }

    public bool Enabled { get; set; }

    public DateTimeOffset? BaselineAt { get; set; }

    public DateTimeOffset? NextRunAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public long Version { get; set; }
}
