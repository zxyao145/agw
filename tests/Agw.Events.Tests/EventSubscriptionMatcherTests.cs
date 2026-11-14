using System.Text.Json;
using Agw.Events.Application.Matching;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Xunit;

namespace Agw.Events.Tests;

public sealed class EventSubscriptionMatcherTests
{
    [Fact]
    public void Match_TypePatternAndJsonLogic_SelectsSubscriptions()
    {
        using var payload = JsonDocument.Parse("""{"state":"open"}""");
        var source = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Webhook,
        };
        var eventRecord = new EventRecord
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            SourceId = source.Id,
            CeSource = "/webhooks/issues",
            CeId = "issue-42",
            Type = "com.github.issue.updated",
            CeDataContentType = "application/json",
            CeData = payload.RootElement.GetRawText(),
            TagsJson = "[\"state:open\"]",
        };
        var matching = CreateSubscription(
            eventTypes: "[\"com.github.issue.*\"]",
            filter: "{\"==\":[{\"var\":\"data.state\"},\"open\"]}"
        );
        var wrongType = CreateSubscription(eventTypes: "[\"com.github.pull_request.*\"]");
        var wrongFilter = CreateSubscription(
            eventTypes: "[\"com.github.issue.*\"]",
            filter: "{\"==\":[{\"var\":\"data.state\"},\"closed\"]}"
        );

        var result = EventSubscriptionMatcher.Match(eventRecord, source, [matching, wrongType, wrongFilter]);

        Assert.Equal(new[] { matching.Id }, result.Select(subscription => subscription.Id));
    }

    [Fact]
    public void Match_UnknownJsonLogicOperator_DoesNotMatch()
    {
        var source = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Webhook,
        };
        var eventRecord = CreateEvent(source, "example.updated");
        var subscription = CreateSubscription(eventTypes: "[\"example.updated\"]", filter: "{\"unknownop\":[true]}");

        var result = EventSubscriptionMatcher.Match(eventRecord, source, [subscription]);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("""{"===":["example.updated"]}""")]
    [InlineData("""{"===":[{"var":"type"},"example.deleted"],"note":"x"}""")]
    [InlineData("""{"===":[{"var":"type"},"example.updated"],"===":[{"var":"type"},"example.deleted"]}""")]
    [InlineData("\"example.updated\"")]
    public void Match_InvalidJsonLogicFilter_DoesNotDropGoodSubscriptions(string filter)
    {
        var source = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Webhook,
        };
        var eventRecord = CreateEvent(source, "example.updated");
        var invalid = CreateSubscription(eventTypes: "[\"example.updated\"]", filter: filter);
        var matching = CreateSubscription(
            eventTypes: "[\"example.updated\"]",
            filter: "{\"===\":[{\"var\":\"type\"},\"example.updated\"]}"
        );

        var result = EventSubscriptionMatcher.Match(eventRecord, source, [invalid, matching]);

        Assert.Equal(new[] { matching.Id }, result.Select(subscription => subscription.Id));
    }

    [Fact]
    public void Match_PublisherKeys_IgnoresExternalSourceWithSameType()
    {
        var source = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Publisher,
            PublisherKey = "ext:other-extension",
        };
        var eventRecord = CreateEvent(source, "com.example.updated");
        var subscription = CreateSubscription(
            eventTypes: "[\"com.example.updated\"]",
            publisherKeys: "[\"ext:trusted-extension\"]"
        );

        var result = EventSubscriptionMatcher.Match(eventRecord, source, [subscription]);

        Assert.Empty(result);
    }

    [Fact]
    public void Match_ManagedSubscriptionWithEmptyPublisherKeys_RejectsForeignPublisher()
    {
        var ownExtensionId = Guid.CreateVersion7();
        var subscription = CreateSubscription(
            eventTypes: "[\"com.example.updated\"]",
            publisherKeys: "[]",
            managedBy: EventManagedBy.ForExtension(ownExtensionId)
        );
        var foreignSource = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Publisher,
            PublisherKey = "ext:foreign-extension",
        };
        var trustedSource = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Publisher,
            PublisherKey = $"ext:{ownExtensionId}",
        };
        var webhookSource = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Webhook,
        };

        var foreignMatches = EventSubscriptionMatcher.Match(
            CreateEvent(foreignSource, "com.example.updated"),
            foreignSource,
            [subscription]
        );
        var trustedMatches = EventSubscriptionMatcher.Match(
            CreateEvent(trustedSource, "com.example.updated"),
            trustedSource,
            [subscription]
        );
        var webhookMatches = EventSubscriptionMatcher.Match(
            CreateEvent(webhookSource, "com.example.updated"),
            webhookSource,
            [subscription]
        );

        Assert.Empty(foreignMatches);
        Assert.Equal(new[] { subscription.Id }, trustedMatches.Select(matched => matched.Id));
        Assert.Equal(new[] { subscription.Id }, webhookMatches.Select(matched => matched.Id));
    }

    private static EventRecord CreateEvent(EventSource source, string type) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            SourceId = source.Id,
            CeSource = "/agw/events",
            CeId = Guid.CreateVersion7().ToString(),
            Type = type,
        };

    private static EventSubscription CreateSubscription(
        string eventTypes,
        string? filter = null,
        string publisherKeys = "[]",
        string managedBy = EventManagedBy.User
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Name = "subscription",
            Enabled = true,
            SourceIdsJson = "[]",
            PublisherKeysJson = publisherKeys,
            EventTypesJson = eventTypes,
            FilterJson = filter,
            TargetKind = EventSubscriptionTargetKind.ExtensionHandler,
            ManagedBy = managedBy,
            ManagedState = EventManagedBy.IsExtension(managedBy) ? EventSubscriptionManagedState.Active : null,
        };
}
