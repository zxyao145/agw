using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Json.Logic;

namespace Agw.Events.Application.Persistence;

public static class EventWebhookMapper
{
    public static CloudEventDraft MapMapped(
        EventSource source,
        EventWebhookMappingConfig mapping,
        WebhookRequestContext request
    )
    {
        var context = CreateContext(request);
        var id = ReadRequiredString(Evaluate(mapping.Id, context));
        var type = ReadRequiredString(Evaluate(mapping.Type, context));
        var tags = MapTags(mapping.Tags, context);

        return new CloudEventDraft
        {
            Id = id,
            Type = type,
            Source = source.CeSource,
            Subject = MapOptionalString(mapping.Subject, context),
            Time = MapTime(mapping.Time, context),
            DataContentType = request.DataContentType,
            DataSchema = MapOptionalString(mapping.DataSchema, context),
            Data = GetData(request),
            Scope = MapOptionalString(mapping.Scope, context),
            Tags = tags,
            Summary = MapOptionalString(mapping.Summary, context),
            Link = MapOptionalString(mapping.Link, context),
        };
    }

    private static JsonObject CreateContext(WebhookRequestContext request)
    {
        var headers = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
        foreach (var (name, value) in request.Headers)
        {
            headers[name] = value;
        }

        return new JsonObject { ["headers"] = headers, ["body"] = GetBodyNode(request) };
    }

    private static JsonNode? GetBodyNode(WebhookRequestContext request)
    {
        if (request.BodyJson is { } json)
        {
            return JsonNode.Parse(json.GetRawText());
        }

        if (request.BodyText is not null)
        {
            return JsonValue.Create(request.BodyText);
        }

        return null;
    }

    private static CloudEventData? GetData(WebhookRequestContext request)
    {
        if (request.BodyJson is { } json)
        {
            return new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = json.GetRawText(),
                Json = json.Clone(),
            };
        }

        if (request.BodyText is not null)
        {
            return new CloudEventData { Kind = CloudEventDataKind.Text, Text = request.BodyText };
        }

        if (request.BodyBinary is not null)
        {
            return new CloudEventData { Kind = CloudEventDataKind.Binary, Binary = request.BodyBinary.ToArray() };
        }

        return null;
    }

    private static IReadOnlyList<string>? MapTags(JsonElement? tagRules, JsonNode context)
    {
        if (tagRules is not { ValueKind: JsonValueKind.Array } tagsElement)
        {
            return null;
        }

        var tags = new List<string>();
        foreach (var tagRule in tagsElement.EnumerateArray())
        {
            var value = ReadOptionalString(Evaluate(tagRule, context));
            if (value is not null)
            {
                tags.Add(value);
            }
        }

        return tags;
    }

    private static DateTimeOffset? MapTime(JsonElement? rule, JsonNode context)
    {
        var value = MapOptionalString(rule, context);
        if (value is null)
        {
            return null;
        }

        if (
            !HasExplicitOffset(value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Mapped CloudEvents time must be an RFC 3339 timestamp.");
        }

        return time;
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var timeSeparatorIndex = value.IndexOf("T", StringComparison.OrdinalIgnoreCase);
        return timeSeparatorIndex >= 0 && value.AsSpan(timeSeparatorIndex + 1).IndexOfAny('+', '-') >= 0;
    }

    private static string? MapOptionalString(JsonElement? rule, JsonNode context) =>
        rule is { } value ? ReadOptionalString(Evaluate(value, context)) : null;

    private static string ReadRequiredString(JsonNode? value) =>
        ReadOptionalString(value) ?? throw new AgwException(ErrorCodes.InvalidParam);

    private static string? ReadOptionalString(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(value.ToJsonString());
        if (document.RootElement.ValueKind != JsonValueKind.String)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Mapped CloudEvents attributes must be strings.");
        }

        return document.RootElement.GetString();
    }

    private static JsonNode? Evaluate(JsonElement rule, JsonNode context)
    {
        var jsonRule = JsonNode.Parse(rule.GetRawText()) ?? throw new AgwException(ErrorCodes.InvalidParam);
        return JsonLogic.Apply(jsonRule, context.DeepClone());
    }
}
