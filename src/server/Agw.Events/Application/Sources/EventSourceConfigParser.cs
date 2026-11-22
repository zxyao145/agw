using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using Agw.Events.Configuration;
using Agw.Events.Domain.ValueObjects;
using Agw.Extensions.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Cronos;
using Json.Logic;
using Json.Path;

namespace Agw.Events.Application.Sources;

public static class EventSourceConfigParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static EventWebhookSourceConfig ParseWebhook(JsonElement config)
    {
        var parsed = Deserialize<EventWebhookSourceConfig>(config);
        ValidateTypeDeclaration(parsed.EventTypes, parsed.FixedType);
        var supportedFormat = parsed.Format is EventSourceFormats.Mapped or EventSourceFormats.CloudEvents;
        var supportedAuth = parsed.Auth is { Kind: "HmacSha256" or "BearerToken" };
        if (!supportedFormat || !supportedAuth || parsed.ContentTypes is null)
            throw new AgwException(ErrorCodes.InvalidParam, "Webhook source configuration is invalid.");

        var contentTypes =
            parsed.ContentTypes.Count > 0 ? parsed.ContentTypes
            : parsed.Format == EventSourceFormats.CloudEvents ? ["application/cloudevents+json", "application/json"]
            : ["application/json"];
        if (contentTypes.Any(contentType => !MediaTypeHeaderValue.TryParse(contentType, out _)))
            throw new AgwException(ErrorCodes.InvalidParam, "Webhook content type is invalid.");

        var mappingIncomplete =
            parsed.Mapping is null
            || parsed.Mapping.Id.ValueKind == JsonValueKind.Undefined
            || parsed.FixedType is null && parsed.Mapping.Type is null or { ValueKind: JsonValueKind.Undefined };
        if (parsed.Format == EventSourceFormats.Mapped && mappingIncomplete)
            throw new AgwException(ErrorCodes.InvalidParam, "Mapped webhook source configuration is incomplete.");

        var auth = NormalizeWebhookAuth(parsed.Auth);
        ValidateWebhookRules(parsed);
        return parsed with { Auth = auth, ContentTypes = contentTypes };
    }

    private static EventWebhookAuthConfig NormalizeWebhookAuth(EventWebhookAuthConfig auth)
    {
        var invalidAuth =
            auth.MaxClockSkewSeconds is < 0 or > 3600
            || auth.TimestampHeader is not null && auth.Kind != "HmacSha256"
            || auth.MaxClockSkewSeconds is not null && auth.TimestampHeader is null;
        if (invalidAuth)
            throw new AgwException(ErrorCodes.InvalidParam, "Webhook authentication configuration is invalid.");

        var tokenHeader = auth.TokenHeader?.Trim();
        return auth.Kind == "BearerToken"
            ? auth with
            {
                TokenHeader = tokenHeader is { Length: > 0 }
                    ? tokenHeader
                    : Microsoft.Net.Http.Headers.HeaderNames.Authorization,
            }
            : auth;
    }

    private static void ValidateWebhookRules(EventWebhookSourceConfig parsed)
    {
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
    }

    public static EventHttpPollSourceConfig ParseHttpPoll(JsonElement config, EventHttpPollOptions options)
    {
        var parsed = Deserialize<EventHttpPollSourceConfig>(config);
        ValidateTypeDeclaration(parsed.EventTypes, parsed.FixedType);
        var supportedFormat = parsed.Format is EventSourceFormats.Mapped or EventSourceFormats.CloudEvents;
        if (!supportedFormat || parsed.Request is null)
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll source configuration is invalid.");

        ValidateHttpPollRequestLocation(parsed.Request);
        var mapped = parsed.Format == EventSourceFormats.Mapped;
        if (mapped)
            ValidateHttpPollMappingShape(parsed.Mapping);
        var pagination = NormalizeHttpPollPagination(parsed, options.MaxPages);
        ValidateHttpPollAuth(parsed.Request.Auth);
        if (mapped)
            ValidateHttpPollMappingRules(parsed, parsed.Mapping!);
        ValidateHttpPollInterval(parsed.Interval, options.MinInterval);
        return parsed with { Pagination = pagination };
    }

    private static void ValidateHttpPollRequestLocation(EventHttpPollRequestConfig request)
    {
        var supportedMethod =
            string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase);
        if (!supportedMethod)
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll method must be GET or POST.");

        var validUri =
            Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            && uri.Scheme is ("http" or "https")
            && string.IsNullOrEmpty(uri.UserInfo);
        if (!validUri)
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll URL must be an absolute HTTP or HTTPS URL.");
    }

    private static void ValidateHttpPollMappingShape(EventHttpPollMappingConfig? mapping)
    {
        var validMapping =
            mapping is not null && IsValidJsonPath(mapping.Items) && mapping.Key.ValueKind != JsonValueKind.Undefined;
        if (!validMapping)
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll mapping is invalid.");
    }

    private static void ValidateHttpPollAuth(EventHttpPollAuthConfig? auth)
    {
        var validAuth = auth?.Kind switch
        {
            "none" or "bearer" or "basic" => true,
            "connection" => auth.ConnectionId is not null,
            "header" => !string.IsNullOrWhiteSpace(auth.HeaderName),
            _ => false,
        };
        if (!validAuth)
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll authentication configuration is invalid.");
    }

    private static void ValidateHttpPollMappingRules(
        EventHttpPollSourceConfig config,
        EventHttpPollMappingConfig mapping
    )
    {
        var change = mapping.Change;
        var validChange = change is { Kind: "content" } or { Kind: "field", Value: not null };
        var eventMapping = mapping.Event;
        var invalidEventTypes =
            config.FixedType is null
            && (
                !DeclaredTypeMatches(config.EventTypes!, eventMapping?.TypeOnNew)
                || !DeclaredTypeMatches(config.EventTypes!, eventMapping?.TypeOnChange)
            );
        if (!validChange || eventMapping is null || invalidEventTypes)
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll change or event configuration is invalid.");

        ValidateRule(change!.Value);
        ValidateRule(mapping.Key);
        ValidateRule(eventMapping.Subject);
        ValidateRule(eventMapping.Time);
        ValidateRule(eventMapping.Tags);
        ValidateRule(eventMapping.Summary);
        ValidateRule(eventMapping.Link);
    }

    private static void ValidateHttpPollInterval(string intervalText, TimeSpan minimum)
    {
        TimeSpan interval;
        try
        {
            interval = XmlConvert.ToTimeSpan(intervalText);
        }
        catch (FormatException exception)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "HTTP poll interval must be an ISO 8601 duration.",
                exception
            );
        }

        if (interval < minimum)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll interval is below the configured minimum.");
        }
    }

    private static EventHttpPollPaginationConfig NormalizeHttpPollPagination(
        EventHttpPollSourceConfig config,
        int maxPages
    )
    {
        var pagination = config.Pagination;
        var usesCursor = pagination.Kind == EventHttpPollPaginationConfig.Cursor;
        var supportedKind =
            pagination.Kind
            is EventHttpPollPaginationConfig.None
                or EventHttpPollPaginationConfig.LinkHeader
                or EventHttpPollPaginationConfig.Page
                or EventHttpPollPaginationConfig.Cursor;
        if (!supportedKind || usesCursor && config.Format == EventSourceFormats.CloudEvents)
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll pagination kind is invalid.");

        var invalidPageLimit = pagination.MaxPages is < 1 || pagination.MaxPages > maxPages;
        var invalidCursor =
            usesCursor
            && (!IsValidJsonPath(pagination.CursorPath) || string.IsNullOrWhiteSpace(pagination.CursorParameter));
        if (invalidPageLimit || invalidCursor)
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll pagination configuration is invalid.");

        var pageParameter = pagination.PageParameter?.Trim();
        return pagination with
        {
            PageParameter = pageParameter is { Length: > 0 } ? pageParameter : null,
            CursorParameter = pagination.CursorParameter?.Trim(),
        };
    }

    internal static bool IsValidJsonPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && JsonPath.TryParse(path, out _);

    public static string Serialize<T>(T config) => JsonSerializer.Serialize(config, JsonOptions);

    public static EventProviderSourceConfig ParseProvider(
        JsonElement config,
        ExtensionEventSourceProviderDescriptor provider
    )
    {
        var parsed = Deserialize<EventProviderSourceConfig>(config);
        var invalidConfiguration =
            provider.Configurations.Count == 0 && parsed.ConfigurationId is not null
            || provider.Configurations.Count > 0
                && (
                    string.IsNullOrWhiteSpace(parsed.ConfigurationId)
                    || !provider.Configurations.Any(item => item.Id == parsed.ConfigurationId)
                );
        if (invalidConfiguration)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The selected source configuration is unavailable.");
        }

        return parsed;
    }

    public static EventTimerSourceConfig ParseTimer(JsonElement config) => ParseTimerWithInterval(config).Config;

    internal static (EventTimerSourceConfig Config, EventTimerInterval? Interval) ParseTimerWithInterval(
        JsonElement config
    )
    {
        var parsed = Deserialize<EventTimerSourceConfig>(config);
        if (
            parsed.Kind is not ("Once" or "Interval" or "Cron")
            || string.IsNullOrWhiteSpace(parsed.TimeZone)
            || string.IsNullOrWhiteSpace(parsed.Event.Type)
            || EventTypePatterns.IsReserved(parsed.Event.Type)
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

        EventTimerInterval? interval = null;
        if (parsed.Kind == "Interval")
        {
            var parsedInterval = ParseInterval(parsed.Interval);
            if (parsedInterval.Duration <= TimeSpan.Zero)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Timer source interval must be positive.");
            }
            interval = parsedInterval;
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

        return (parsed, interval);
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

    internal static T Deserialize<T>(JsonElement config)
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

    private static void ValidateTypeDeclaration(IReadOnlyList<string>? eventTypes, string? fixedType)
    {
        var validDeclaration = (eventTypes, fixedType) switch
        {
            ({ Count: > 0 } patterns, null) => patterns.All(EventTypePatterns.IsDeclaredExternalPattern),
            (null, { } type) => EventTypePatterns.IsExactType(type) && !EventTypePatterns.IsReserved(type),
            _ => false,
        };
        if (!validDeclaration)
            throw new AgwException(ErrorCodes.InvalidParam, "Source event type declaration is invalid.");
    }

    private static bool DeclaredTypeMatches(IReadOnlyList<string> eventTypes, string? eventType) =>
        eventType is not null
        && EventTypePatterns.IsExactType(eventType)
        && !EventTypePatterns.IsReserved(eventType)
        && EventTypePatterns.MatchesAny(eventTypes, eventType);

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

    private static EventTimerInterval ParseInterval(string? interval)
    {
        if (string.IsNullOrWhiteSpace(interval))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Timer source interval is required.");
        }

        try
        {
            return new EventTimerInterval { Text = interval, Duration = XmlConvert.ToTimeSpan(interval) };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Timer source interval must be an ISO 8601 duration within the supported range.",
                exception
            );
        }
    }
}
