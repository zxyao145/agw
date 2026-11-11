using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using Agw.Events.Configuration;
using Agw.Shared.Exceptions;
using Cronos;
using Json.Logic;
using Json.Path;

namespace Agw.Events.Application.Persistence;

public static class EventSourceConfigParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static EventWebhookSourceConfig ParseWebhook(JsonElement config)
    {
        var parsed = Deserialize<EventWebhookSourceConfig>(config);
        if (
            parsed.Format is not ("mapped" or "cloudevents")
            || parsed.ContentTypes.Count == 0
            || parsed.Auth.Kind is not ("HmacSha256" or "BearerToken")
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Webhook source configuration is invalid.");
        }

        try
        {
            foreach (var contentType in parsed.ContentTypes)
            {
                _ = MediaTypeHeaderValue.Parse(contentType);
            }
        }
        catch (FormatException exception)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Webhook content type is invalid.", exception);
        }

        if (
            parsed.Format == "mapped"
            && (
                parsed.Mapping is null
                || parsed.Mapping.Id.ValueKind == JsonValueKind.Undefined
                || parsed.Mapping.Type.ValueKind == JsonValueKind.Undefined
            )
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Mapped webhook sources require id and type rules.");
        }

        if (
            parsed.Auth.MaxClockSkewSeconds is < 0
            || parsed.Auth.MaxClockSkewSeconds > 3600
            || parsed.Auth.TimestampHeader is not null && parsed.Auth.Kind != "HmacSha256"
            || parsed.Auth.MaxClockSkewSeconds is not null && parsed.Auth.TimestampHeader is null
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Webhook authentication configuration is invalid.");
        }

        ValidateRule(parsed.Mapping?.Id);
        ValidateRule(parsed.Mapping?.Type);
        ValidateRule(parsed.Mapping?.Subject);
        ValidateRule(parsed.Mapping?.Time);
        ValidateRule(parsed.Mapping?.DataSchema);
        ValidateRule(parsed.Mapping?.Scope);
        ValidateRule(parsed.Mapping?.Tags);
        ValidateRule(parsed.Mapping?.Summary);
        ValidateRule(parsed.Mapping?.Link);
        ValidateRule(parsed.CloudEvent?.Summary);
        ValidateRule(parsed.CloudEvent?.Link);
        return parsed;
    }

    public static EventHttpPollSourceConfig ParseHttpPoll(JsonElement config, EventHttpPollOptions options)
    {
        var parsed = Deserialize<EventHttpPollSourceConfig>(config);
        if (
            !parsed.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && !parsed.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll method must be GET or POST.");
        }

        if (
            !Uri.TryCreate(parsed.Request.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll URL must be an absolute HTTP or HTTPS URL.");
        }

        if (!JsonPath.TryParse(parsed.Items, out _))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll items JSONPath is invalid.");
        }

        if (parsed.Pagination.Kind is not ("none" or "linkHeader" or "cursor" or "page"))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll pagination kind is invalid.");
        }

        if (
            parsed.Pagination.MaxPages is < 1
            || parsed.Pagination.MaxPages > options.MaxPages
            || parsed.Pagination.Kind == "cursor"
                && (
                    string.IsNullOrWhiteSpace(parsed.Pagination.CursorPath)
                    || string.IsNullOrWhiteSpace(parsed.Pagination.CursorParameter)
                )
            || parsed.Pagination.Kind == "cursor" && !JsonPath.TryParse(parsed.Pagination.CursorPath!, out _)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll pagination configuration is invalid.");
        }

        if (
            parsed.Request.Auth.Kind is not ("none" or "bearer" or "basic" or "header" or "connection")
            || parsed.Request.Auth.Kind == "connection" && parsed.Request.Auth.ConnectionId is null
            || parsed.Request.Auth.Kind == "header" && string.IsNullOrWhiteSpace(parsed.Request.Auth.HeaderName)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll authentication configuration is invalid.");
        }

        if (
            parsed.Change.Kind is not ("field" or "content")
            || parsed.Change.Kind == "field" && parsed.Change.Value is null
            || parsed.Event.TypeOnNew.StartsWith("agw.", StringComparison.Ordinal)
            || parsed.Event.TypeOnChange.StartsWith("agw.", StringComparison.Ordinal)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll change or event configuration is invalid.");
        }

        if (parsed.Change.Value is not null)
        {
            ValidateRule(parsed.Change.Value);
        }

        ValidateRule(parsed.Key);
        ValidateRule(parsed.Event.Subject);
        ValidateRule(parsed.Event.Time);
        ValidateRule(parsed.Event.Tags);
        ValidateRule(parsed.Event.Summary);
        ValidateRule(parsed.Event.Link);

        TimeSpan interval;
        try
        {
            interval = XmlConvert.ToTimeSpan(parsed.Interval);
        }
        catch (FormatException exception)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "HTTP poll interval must be an ISO 8601 duration.",
                exception
            );
        }

        if (interval < options.MinInterval)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll interval is below the configured minimum.");
        }

        return parsed;
    }

    public static EventTimerSourceConfig ParseTimer(JsonElement config)
    {
        var parsed = Deserialize<EventTimerSourceConfig>(config);
        if (
            parsed.Kind is not ("Once" or "Interval" or "Cron")
            || string.IsNullOrWhiteSpace(parsed.TimeZone)
            || string.IsNullOrWhiteSpace(parsed.Event.Type)
            || parsed.Event.Type.StartsWith("agw.", StringComparison.Ordinal)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Timer source configuration is invalid.");
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(parsed.TimeZone);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Timer source time zone is invalid.", exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Timer source time zone is invalid.", exception);
        }

        if (parsed.Kind == "Once" && parsed.At is null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Once timer sources require at.");
        }

        if (parsed.Kind == "Interval")
        {
            var interval = ParseInterval(parsed.Interval);
            if (interval <= TimeSpan.Zero)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Timer source interval must be positive.");
            }
        }

        if (parsed.Kind == "Cron")
        {
            if (string.IsNullOrWhiteSpace(parsed.Cron))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Cron timer sources require cron.");
            }

            try
            {
                _ = CronExpression.Parse(parsed.Cron, CronFormat.Standard);
            }
            catch (CronFormatException exception)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Timer source cron expression is invalid.", exception);
            }
        }

        if (parsed.Event.Data is { } data && parsed.Event.DataBase64 is not null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Timer event data can use either data or dataBase64.");
        }

        if (parsed.Event.DataBase64 is not null)
        {
            try
            {
                _ = Convert.FromBase64String(parsed.Event.DataBase64);
            }
            catch (FormatException exception)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Timer event dataBase64 is invalid.", exception);
            }
        }

        return parsed;
    }

    public static JsonElement ParseSecret(string? secretJson)
    {
        if (string.IsNullOrWhiteSpace(secretJson))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Source authentication secret is required.");
        }

        using var document = JsonDocument.Parse(secretJson);
        return document.RootElement.Clone();
    }

    public static void ValidateWebhookSecret(EventWebhookSourceConfig config, JsonElement? secret)
    {
        if (
            secret is not { } value
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(value.GetString())
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Webhook sources require an authentication secret.");
        }
    }

    public static void ValidateHttpPollSecret(EventHttpPollSourceConfig config, JsonElement? secret)
    {
        switch (config.Request.Auth.Kind)
        {
            case "none":
            case "connection":
                if (secret is not null)
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        "This HTTP poll authentication mode does not accept a secret."
                    );
                }
                return;
            case "bearer":
                if (secret is { ValueKind: JsonValueKind.String } token && !string.IsNullOrEmpty(token.GetString()))
                {
                    return;
                }
                throw new AgwException(ErrorCodes.InvalidParam, "Bearer authentication requires a token secret.");
            case "basic":
                if (
                    secret is { ValueKind: JsonValueKind.Object } credentials
                    && credentials.TryGetProperty("username", out var username)
                    && credentials.TryGetProperty("password", out var password)
                    && username.ValueKind == JsonValueKind.String
                    && password.ValueKind == JsonValueKind.String
                )
                {
                    return;
                }
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "Basic authentication requires username and password secrets."
                );
            case "header":
                if (
                    secret is { ValueKind: JsonValueKind.Object } headerSecret
                    && headerSecret.TryGetProperty("value", out var headerValue)
                    && headerValue.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(headerValue.GetString())
                )
                {
                    return;
                }
                throw new AgwException(ErrorCodes.InvalidParam, "Header authentication requires a value secret.");
            default:
                throw new AgwException(ErrorCodes.InvalidParam);
        }
    }

    private static T Deserialize<T>(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Source configuration must be a JSON object.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(config, JsonOptions)
                ?? throw new AgwException(ErrorCodes.InvalidParam, "Source configuration is required.");
        }
        catch (JsonException exception)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Source configuration is invalid.", exception);
        }
    }

    private static void ValidateRule(JsonElement? rule)
    {
        if (rule is { } value)
        {
            var jsonRule = JsonNode.Parse(value.GetRawText()) ?? throw new AgwException(ErrorCodes.InvalidParam);
            try
            {
                _ = JsonLogic.Apply(jsonRule, new JsonObject());
            }
            catch (JsonLogicException exception)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Source JsonLogic expression is invalid.", exception);
            }
        }
    }

    private static TimeSpan ParseInterval(string? interval)
    {
        if (string.IsNullOrWhiteSpace(interval))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Timer source interval is required.");
        }

        try
        {
            return XmlConvert.ToTimeSpan(interval);
        }
        catch (FormatException exception)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Timer source interval must be an ISO 8601 duration.",
                exception
            );
        }
    }
}
