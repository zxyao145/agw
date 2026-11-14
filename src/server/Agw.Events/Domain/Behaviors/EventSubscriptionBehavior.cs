using Agw.Events.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Events;

namespace Agw.Events.Domain.Behaviors;

public sealed class EventSubscriptionBehavior
{
    private readonly EventSubscription _subscription;

    public EventSubscriptionBehavior(EventSubscription subscription)
    {
        _subscription = subscription;
    }

    public bool UpsertManaged(long managedVersion, ManagedEventSubscriptionDefinition definition)
    {
        if (!CanApply(managedVersion))
        {
            return managedVersion == _subscription.ManagedVersion;
        }

        _subscription.Name = definition.Name;
        _subscription.Enabled = true;
        _subscription.SourceIdsJson = definition.SourceIdsJson;
        _subscription.PublisherKeysJson = definition.PublisherKeysJson;
        _subscription.EventTypesJson = definition.EventTypesJson;
        _subscription.FilterJson = definition.FilterJson;
        _subscription.TargetKind = EventSubscriptionTargetKind.ExtensionHandler;
        _subscription.AgentId = null;
        _subscription.ProjectId = null;
        _subscription.Instructions = null;
        _subscription.PermissionMode = null;
        _subscription.HandlerExtensionId = definition.HandlerExtensionId;
        _subscription.HandlerId = definition.HandlerId;
        _subscription.HandlerConfigJson = definition.HandlerConfigJson;
        _subscription.ManagedVersion = managedVersion;
        _subscription.ManagedState = EventSubscriptionManagedState.Active;
        _subscription.Version++;
        return true;
    }

    public bool DeleteManaged(long managedVersion)
    {
        if (!CanApply(managedVersion))
        {
            return managedVersion == _subscription.ManagedVersion;
        }

        _subscription.Enabled = false;
        _subscription.ManagedVersion = managedVersion;
        _subscription.ManagedState = EventSubscriptionManagedState.Deleted;
        _subscription.Version++;
        return true;
    }

    private bool CanApply(long managedVersion) =>
        _subscription.ManagedVersion is null || managedVersion > _subscription.ManagedVersion.Value;
}
