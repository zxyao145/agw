using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Events;

[Table("event")]
[EntityTypeConfiguration(typeof(EventRecordConfiguration))]
public class EventRecord : BaseEntity
{
    public Guid Id { get; set; }

    public Guid SourceId { get; set; }

    public string CeSpecVersion { get; set; } = "1.0";

    public string CeSource { get; set; } = string.Empty;

    public string CeId { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public DateTimeOffset? CeTime { get; set; }

    public string? Subject { get; set; }

    public string? CeDataContentType { get; set; }

    public string? CeDataSchema { get; set; }

    [JsonIgnore]
    [Encrypted]
    public string? CeData { get; set; }

    public bool CeDataIsBinary { get; set; }

    public string? Scope { get; set; }

    public string? TagsJson { get; set; }

    public string? Summary { get; set; }

    public string? Link { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public Guid ChainId { get; set; }

    public Guid? ParentEventId { get; set; }

    public int Hops { get; set; }

    public EventDispatchState DispatchState { get; set; }

    public int MatchedCount { get; set; }

    public DateTimeOffset RecoverAfter { get; set; }
}
