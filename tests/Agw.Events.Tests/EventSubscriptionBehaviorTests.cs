using Agw.Events.Domain.Behaviors;
using Agw.Events.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Events;
using Xunit;

namespace Agw.Events.Tests;

public sealed class EventSubscriptionBehaviorTests
{
    [Fact]
    public void Upsert_ManagedSubscriptionBySequence_IgnoresStaleRequest()
    {
        var subscription = new EventSubscription
        {
            ManagedVersion = 7,
            ManagedState = EventSubscriptionManagedState.Active,
            Version = 2,
            Name = "current",
            EventTypesJson = "[\"com.example.current\"]",
        };
        var behavior = new EventSubscriptionBehavior(subscription);

        var duplicateAccepted = behavior.UpsertManaged(7, Definition("duplicate"));
        var staleAccepted = behavior.UpsertManaged(6, Definition("stale"));
        var staleName = subscription.Name;
        var newerAccepted = behavior.UpsertManaged(8, Definition("newer"));

        Assert.True(duplicateAccepted);
        Assert.False(staleAccepted);
        Assert.Equal("current", staleName);
        Assert.True(newerAccepted);
        Assert.Equal("newer", subscription.Name);
        Assert.Equal(8L, subscription.ManagedVersion);
        Assert.Equal(EventSubscriptionManagedState.Active, subscription.ManagedState);
        Assert.Equal(3, subscription.Version);
    }

    [Fact]
    public void DeleteManaged_BeforeUpsert_CreatesTombstone()
    {
        var subscription = new EventSubscription
        {
            CreateBy = "owner",
            ManagedByExtensionId = Guid.CreateVersion7(),
            ManagedKey = "rule:42",
        };
        var behavior = new EventSubscriptionBehavior(subscription);

        var deleted = behavior.DeleteManaged(9);
        var staleUpsertAccepted = behavior.UpsertManaged(8, Definition("late"));

        Assert.True(deleted);
        Assert.False(staleUpsertAccepted);
        Assert.Equal(9, subscription.ManagedVersion);
        Assert.Equal(EventSubscriptionManagedState.Deleted, subscription.ManagedState);
    }

    private static ManagedEventSubscriptionDefinition Definition(string name) =>
        new()
        {
            Name = name,
            SourceIdsJson = "[]",
            PublisherKeysJson = "[]",
            EventTypesJson = "[\"com.example.updated\"]",
            HandlerId = "handler",
            HandlerConfigJson = "{}",
        };
}
