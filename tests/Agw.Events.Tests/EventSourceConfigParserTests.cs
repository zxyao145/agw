using System.Text.Json;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Shared.Exceptions;
using Xunit;

namespace Agw.Events.Tests;

public sealed class EventSourceConfigParserTests
{
    [Fact]
    public void ParseHttpPoll_ValidConfiguration_ReturnsIntervalAndKeyRule()
    {
        using var document = JsonDocument.Parse(
            """{"request":{"method":"GET","url":"https://example.test/issues","auth":{"kind":"none"}},"items":"$.items[*]","key":{"var":"item.id"},"change":{"kind":"content"},"event":{"typeOnNew":"issue.created","typeOnChange":"issue.updated"},"interval":"PT2M"}"""
        );

        var result = EventSourceConfigParser.ParseHttpPoll(document.RootElement, new EventHttpPollOptions());

        Assert.Equal("$.items[*]", result.Items);
        Assert.Equal("content", result.Change.Kind);
        Assert.Equal("PT2M", result.Interval);
    }

    [Fact]
    public void ParseHttpPoll_BearerAuthenticationWithoutSecret_ThrowsInvalidParam()
    {
        using var document = JsonDocument.Parse(
            """{"request":{"method":"GET","url":"https://example.test/issues","auth":{"kind":"bearer"}},"items":"$.items[*]","key":{"var":"item.id"},"change":{"kind":"content"},"event":{"typeOnNew":"issue.created","typeOnChange":"issue.updated"},"interval":"PT2M"}"""
        );
        var config = EventSourceConfigParser.ParseHttpPoll(document.RootElement, new EventHttpPollOptions());

        Assert.Throws<AgwException>(() => EventSourceConfigParser.ValidateHttpPollSecret(config, null));
    }

    [Fact]
    public void ParseWebhook_MappedFormatWithoutRequiredRules_ThrowsInvalidParam()
    {
        using var document = JsonDocument.Parse(
            """{"format":"mapped","auth":{"kind":"BearerToken"},"contentTypes":["application/json"],"mapping":{"subject":{"var":"body.subject"}}}"""
        );

        Assert.Throws<AgwException>(() => EventSourceConfigParser.ParseWebhook(document.RootElement));
    }
}
