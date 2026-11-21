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
    public void Match_SourceIdsContainBuiltInAndJobTimer_MatchesBoth()
    {
        var builtIn = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Publisher,
            ManagedBy = EventManagedBy.Agw,
            Name = EventBuiltInSources.Execution,
        };
        var timer = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Timer,
            ManagedBy = EventManagedBy.Job,
        };
        var webhook = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Webhook,
        };
        var subscription = CreateSubscription(
            eventTypes: "[\"agw.execution.completed\",\"job.timer.fired\"]",
            sourceIds: [builtIn.Id, timer.Id]
        );

        Assert.Single(
            EventSubscriptionMatcher.Match(CreateEvent(builtIn, "agw.execution.completed"), builtIn, [subscription])
        );
        Assert.Single(EventSubscriptionMatcher.Match(CreateEvent(timer, "job.timer.fired"), timer, [subscription]));
        Assert.Empty(EventSubscriptionMatcher.Match(CreateEvent(webhook, "job.timer.fired"), webhook, [subscription]));
    }

    [Fact]
    public void Match_ExtensionSubscriptionWithOtherExtensionSource_DoesNotMatch()
    {
        var ownExtensionId = Guid.CreateVersion7();
        var subscription = CreateSubscription(
            eventTypes: "[\"com.example.updated\"]",
            managedBy: EventManagedBy.ForExtension(ownExtensionId)
        );
        var foreignSource = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Publisher,
            ManagedBy = EventManagedBy.ForExtension(Guid.CreateVersion7()),
        };
        var trustedSource = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Publisher,
            ManagedBy = EventManagedBy.ForExtension(ownExtensionId),
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

    [Theory]
    [InlineData("""{"and":[{"in":[{"var":"subject"},["issue-42"]]}]}""")]
    [InlineData("""{"and":[{"in":[{"var":"scope"},["project-1"]]}]}""")]
    [InlineData("""{"and":[{"some":[{"var":"tags"},{"in":[{"var":""},["urgent"]]}]}]}""")]
    [InlineData("""{"and":[{"in":[{"cat":[{"var":"data.status"}]},["open"]]}]}""")]
    public void Match_GeneratedFilters_EvaluateExpectedResults(string filter)
    {
        var source = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Webhook,
        };
        var record = CreateEvent(source, "demo.updated");
        record.Subject = "issue-42";
        record.Scope = "project-1";
        record.TagsJson = "[\"urgent\"]";
        record.CeDataContentType = "application/json";
        record.CeData = "{\"status\":\"open\"}";
        var subscription = CreateSubscription(eventTypes: "[\"demo.updated\"]", filter: filter);

        Assert.Single(EventSubscriptionMatcher.Match(record, source, [subscription]));
        record.Scope = "other-project";
        record.Subject = "other-issue";
        record.TagsJson = "[]";
        record.CeData = "{\"status\":\"closed\"}";
        Assert.Empty(EventSubscriptionMatcher.Match(record, source, [subscription]));
    }

    [Fact]
    public void Match_GeneratedFilters_MatchesEitherScopeAndNumericDataValue()
    {
        var source = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Kind = EventSourceKind.Webhook,
        };
        var record = CreateEvent(source, "demo.updated");
        record.CeDataContentType = "application/json";
        var subscription = CreateSubscription(
            eventTypes: "[\"demo.updated\"]",
            filter: """{"and":[{"in":[{"var":"scope"},["proj1","proj2"]]},{"in":[{"cat":[{"var":"data.count"}]},["1","2"]]}]}"""
        );

        record.Scope = "proj1";
        record.CeData = "{\"count\":1}";
        Assert.Single(EventSubscriptionMatcher.Match(record, source, [subscription]));

        record.Scope = "proj2";
        record.CeData = "{\"count\":2}";
        Assert.Single(EventSubscriptionMatcher.Match(record, source, [subscription]));

        record.Scope = "proj3";
        Assert.Empty(EventSubscriptionMatcher.Match(record, source, [subscription]));

        record.Scope = "proj1";
        record.CeData = "{\"count\":3}";
        Assert.Empty(EventSubscriptionMatcher.Match(record, source, [subscription]));
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
        IReadOnlyList<Guid>? sourceIds = null,
        string managedBy = EventManagedBy.User
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Name = "subscription",
            Enabled = true,
            SourceIdsJson = JsonSerializer.Serialize(sourceIds ?? []),
            EventTypesJson = eventTypes,
            FilterJson = filter,
            TargetKind = EventSubscriptionTargetKind.ExtensionHandler,
            ManagedBy = managedBy,
            ManagedState = EventManagedBy.IsExtension(managedBy) ? EventSubscriptionManagedState.Active : null,
        };
}
