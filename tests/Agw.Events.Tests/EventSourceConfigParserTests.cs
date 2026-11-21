using System.Text.Json;
using Agw.Events.Application.Sources;
using Agw.Events.Configuration;
using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;
using Xunit;

namespace Agw.Events.Tests;

public sealed class EventSourceConfigParserTests
{
    [Fact]
    public void ParseHttpPoll_ValidConfiguration_ReturnsIntervalAndKeyRule()
    {
        using var document = JsonDocument.Parse(
            """{"eventTypes":["issue.*"],"format":"mapped","request":{"method":"GET","url":"https://example.test/issues","auth":{"kind":"none"}},"mapping":{"items":"$.items[*]","key":{"var":"item.id"},"change":{"kind":"content"},"event":{"typeOnNew":"issue.created","typeOnChange":"issue.updated"}},"interval":"PT2M"}"""
        );

        var result = EventSourceConfigParser.ParseHttpPoll(document.RootElement, new EventHttpPollOptions());

        Assert.Equal("$.items[*]", result.Mapping!.Items);
        Assert.Equal("content", result.Mapping.Change.Kind);
        Assert.Equal("PT2M", result.Interval);
    }

    [Fact]
    public void ParseHttpPoll_BearerAuthenticationWithoutSecret_ThrowsInvalidParam()
    {
        using var document = JsonDocument.Parse(
            """{"eventTypes":["issue.*"],"format":"mapped","request":{"method":"GET","url":"https://example.test/issues","auth":{"kind":"bearer"}},"mapping":{"items":"$.items[*]","key":{"var":"item.id"},"change":{"kind":"content"},"event":{"typeOnNew":"issue.created","typeOnChange":"issue.updated"}},"interval":"PT2M"}"""
        );
        var config = EventSourceConfigParser.ParseHttpPoll(document.RootElement, new EventHttpPollOptions());

        Assert.Throws<AgwException>(() => EventSourceConfigParser.ValidateHttpPollSecret(config, null));
    }

    [Fact]
    public void ParseWebhook_MappedFormatWithoutRequiredRules_ThrowsInvalidParam()
    {
        using var document = JsonDocument.Parse(
            """{"eventTypes":["demo.ticket.*"],"format":"mapped","mapping":{"subject":{"var":"body.subject"}}}"""
        );

        Assert.Throws<AgwException>(() => EventSourceConfigParser.ParseWebhook(document.RootElement));
    }

    [Fact]
    public void ParseWebhook_OnlyTypes_FillsDefaults()
    {
        using var document = JsonDocument.Parse("""{"eventTypes":["demo.ticket.*"]}""");

        var result = EventSourceConfigParser.ParseWebhook(document.RootElement);

        Assert.Equal("cloudevents", result.Format);
        Assert.Equal("BearerToken", result.Auth.Kind);
        Assert.Equal("Authorization", result.Auth.TokenHeader);
        Assert.Equal(["application/cloudevents+json", "application/json"], result.ContentTypes);
        Assert.Equal(["demo.ticket.*"], result.EventTypes);
    }

    [Theory]
    [InlineData(null, "Authorization")]
    [InlineData("", "Authorization")]
    [InlineData("  ", "Authorization")]
    [InlineData(" X-Webhook-Token ", "X-Webhook-Token")]
    public void ParseWebhook_TokenHeader_NormalizesName(string? tokenHeader, string expected)
    {
        var config = JsonSerializer.SerializeToElement(new { fixedType = "demo.created", auth = new { tokenHeader } });

        var result = EventSourceConfigParser.ParseWebhook(config);

        Assert.Equal(expected, result.Auth.TokenHeader);
    }

    [Theory]
    [InlineData("""{"eventTypes":["demo.ticket.*"],"fixedType":"demo.ticket.created"}""")]
    [InlineData("""{}""")]
    [InlineData("""{"eventTypes":[]}""")]
    [InlineData("""{"eventTypes":[],"fixedType":"demo.ticket.created"}""")]
    public void ParseWebhook_TypeDeclarationMissingOrAmbiguous_ThrowsInvalidParam(string json)
    {
        using var document = JsonDocument.Parse(json);

        var error = Assert.Throws<AgwException>(() => EventSourceConfigParser.ParseWebhook(document.RootElement));

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Theory]
    [InlineData("demo.*")]
    [InlineData("agw.demo")]
    [InlineData("demo ticket")]
    public void ParseWebhook_FixedTypeInvalid_ThrowsInvalidParam(string fixedType)
    {
        var config = JsonSerializer.SerializeToElement(new { fixedType });

        var error = Assert.Throws<AgwException>(() => EventSourceConfigParser.ParseWebhook(config));

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Fact]
    public void ParseWebhook_MappedWithFixedType_TypeRuleOptional()
    {
        using var document = JsonDocument.Parse(
            """{"fixedType":"demo.ticket.created","format":"mapped","mapping":{"id":{"var":"headers.X-Id"}}}"""
        );

        var result = EventSourceConfigParser.ParseWebhook(document.RootElement);

        Assert.Equal("demo.ticket.created", result.FixedType);
        Assert.Null(result.Mapping!.Type);
        Assert.Equal(["application/json"], result.ContentTypes);
    }

    [Fact]
    public void ParseHttpPoll_OnlyRequiredFields_FillsDefaults()
    {
        using var document = JsonDocument.Parse(
            """{"eventTypes":["demo.ticket.*"],"request":{"url":"https://example.test/events"},"interval":"PT5M"}"""
        );

        var result = EventSourceConfigParser.ParseHttpPoll(document.RootElement, new EventHttpPollOptions());

        Assert.Equal("cloudevents", result.Format);
        Assert.Equal("GET", result.Request.Method);
        Assert.Equal("none", result.Request.Auth.Kind);
        Assert.Equal("none", result.Pagination.Kind);
        Assert.Null(result.Mapping);
    }

    [Theory]
    [InlineData(" pageNumber ", "pageNumber")]
    [InlineData("  ", null)]
    public void ParseHttpPoll_PagePagination_NormalizesOptionalParameter(string pageParameter, string? expected)
    {
        var config = JsonSerializer.SerializeToElement(
            new
            {
                fixedType = "demo.created",
                request = new { url = "https://example.test/events" },
                interval = "PT5M",
                pagination = new { kind = "page", pageParameter },
            }
        );

        var result = EventSourceConfigParser.ParseHttpPoll(config, new EventHttpPollOptions());

        Assert.Equal(expected, result.Pagination.PageParameter);
    }

    [Fact]
    public void ParseHttpPoll_CursorPagination_TrimsParameter()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "fixedType": "demo.created",
              "format": "mapped",
              "request": { "url": "https://example.test/events" },
              "interval": "PT5M",
              "mapping": { "items": "$.items[*]", "key": {"var": "item.id"}, "change": {"kind": "content"}, "event": {} },
              "pagination": { "kind": "cursor", "cursorPath": "$.next", "cursorParameter": " cursor " }
            }
            """
        );

        var result = EventSourceConfigParser.ParseHttpPoll(document.RootElement, new EventHttpPollOptions());

        Assert.Equal("cursor", result.Pagination.CursorParameter);
    }

    [Fact]
    public void ParseHttpPoll_MappedTypesOutsideDeclaration_ThrowsInvalidParam()
    {
        using var document = JsonDocument.Parse(
            """{"eventTypes":["shop.order.*"],"format":"mapped","request":{"url":"https://example.test/events"},"interval":"PT5M","mapping":{"items":"$.items[*]","key":{"var":"item.id"},"change":{"kind":"content"},"event":{"typeOnNew":"shop.customer.created","typeOnChange":"shop.order.updated"}}}"""
        );

        var error = Assert.Throws<AgwException>(() =>
            EventSourceConfigParser.ParseHttpPoll(document.RootElement, new EventHttpPollOptions())
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Fact]
    public void ParseHttpPoll_CloudEventsWithCursorPagination_ThrowsInvalidParam()
    {
        using var document = JsonDocument.Parse(
            """{"eventTypes":["demo.ticket.*"],"request":{"url":"https://example.test/events"},"interval":"PT5M","pagination":{"kind":"cursor","cursorPath":"$.next","cursorParameter":"cursor"}}"""
        );

        var error = Assert.Throws<AgwException>(() =>
            EventSourceConfigParser.ParseHttpPoll(document.RootElement, new EventHttpPollOptions())
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"configurationId":"missing"}""")]
    public void ParseProvider_WithConfigurations_RequiresDeclaredConfigurationId(string json)
    {
        using var document = JsonDocument.Parse(json);
        var provider = new ExtensionEventSourceProviderDescriptor
        {
            Id = "source",
            EventTypes = ["demo.*"],
            Configurations =
            [
                new ExtensionEventSourceConfiguration
                {
                    Id = "default",
                    Name = "Default",
                    Config = JsonSerializer.SerializeToElement(new { value = "selected" }),
                },
            ],
        };

        var error = Assert.Throws<AgwException>(() =>
            EventSourceConfigParser.ParseProvider(document.RootElement, provider)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Fact]
    public void ParseProvider_WithoutConfigurations_RejectsConfigurationId()
    {
        using var document = JsonDocument.Parse("""{"configurationId":"default"}""");
        var provider = new ExtensionEventSourceProviderDescriptor { Id = "source", EventTypes = ["demo.*"] };

        var error = Assert.Throws<AgwException>(() =>
            EventSourceConfigParser.ParseProvider(document.RootElement, provider)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Fact]
    public void Serialize_ProviderWithoutConfiguration_OmitsConfigurationId()
    {
        var config = new EventProviderSourceConfig();

        Assert.Equal("{}", EventSourceConfigParser.Serialize(config));
    }
}
