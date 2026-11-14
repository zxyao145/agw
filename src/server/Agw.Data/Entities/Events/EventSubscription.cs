using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Events;

[Table("event_subscription")]
[EntityTypeConfiguration(typeof(EventSubscriptionConfiguration))]
public class EventSubscription : BaseEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public string SourceIdsJson { get; set; } = "[]";

    public string? PublisherKeysJson { get; set; }

    public string EventTypesJson { get; set; } = "[]";

    public string? FilterJson { get; set; }

    public EventSubscriptionTargetKind TargetKind { get; set; }

    public Guid? AgentId { get; set; }

    public Guid? ProjectId { get; set; }

    public string? Instructions { get; set; }

    public int? PermissionMode { get; set; }

    public Guid? HandlerExtensionId { get; set; }

    public string? HandlerId { get; set; }

    public string? HandlerConfigJson { get; set; }

    public string ManagedBy { get; set; } = EventManagedBy.User;

    public string? ManagedKey { get; set; }

    public long? ManagedVersion { get; set; }

    public EventSubscriptionManagedState? ManagedState { get; set; }

    public long Version { get; set; }
}
