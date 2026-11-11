using System.Text.Json;
using Agw.Events.Application.Persistence;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Xunit;

namespace Agw.Events.Tests;

public sealed class EventWebhookMapperTests
{
    [Fact]
    public void Map_WebhookHeadersAndBody_ProducesCloudEventDraft()
    {
        using var body = JsonDocument.Parse(
            """{"action":"opened","issue":{"number":42,"title":"Login fails","state":"open","html_url":"https://example.test/issues/42","updated_at":"2026-11-08T09:31:00Z"}}"""
        );
        var source = new EventSource { Id = Guid.CreateVersion7(), CeSource = "/agw/sources/issues" };
        var config = new EventWebhookMappingConfig
        {
            Id = Rule("""{"var":"headers.X-GitHub-Delivery"}"""),
            Type = Rule("""{"cat":["com.github.issue.",{"var":"body.action"}]}"""),
            Subject = Rule("""{"cat":["acme/app#",{"var":"body.issue.number"}]}"""),
            Time = Rule("""{"var":"body.issue.updated_at"}"""),
            Tags = Rule("""[{"cat":["state:",{"var":"body.issue.state"}]}]"""),
            Summary = Rule("""{"var":"body.issue.title"}"""),
            Link = Rule("""{"var":"body.issue.html_url"}"""),
        };
        var request = new WebhookRequestContext
        {
            Headers = new Dictionary<string, string> { ["X-GitHub-Delivery"] = "delivery-42" },
            BodyJson = body.RootElement.Clone(),
            DataContentType = "application/json",
        };

        var draft = EventWebhookMapper.MapMapped(source, config, request);

        Assert.Equal("delivery-42", draft.Id);
        Assert.Equal("com.github.issue.opened", draft.Type);
        Assert.Equal("/agw/sources/issues", draft.Source);
        Assert.Equal("acme/app#42", draft.Subject);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 9, 31, 0, TimeSpan.Zero), draft.Time);
        Assert.Equal(new[] { "state:open" }, draft.Tags);
        Assert.Equal("Login fails", draft.Summary);
        Assert.Equal("https://example.test/issues/42", draft.Link);
        Assert.Equal(CloudEventDataKind.Json, draft.Data!.Kind);
        Assert.NotNull(draft.Data.Json);
        Assert.Equal(body.RootElement.GetRawText(), draft.Data.Json.Value.GetRawText());
    }

    private static JsonElement Rule(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
