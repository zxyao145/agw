using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Events.Application.Sources;
using Agw.Events.Configuration;
using Agw.Events.Contracts.Transport;
using Agw.Integrations.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Json.Logic;
using Json.Path;
using Meziantou.Framework.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Agw.Events.Infrastructure.Sources;

public sealed class EventHttpPoller
{
    public const string HttpClientName = "Agw.Events.HttpPoll";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICloudEventEnvelopeSerializer _serializer;
    private readonly IConnectionAuthorizationFacade? _connectionAuthorization;
    private readonly EventHttpPollOptions _options;

    public EventHttpPoller(
        IHttpClientFactory httpClientFactory,
        IOptions<EventsOptions> options,
        ICloudEventEnvelopeSerializer serializer,
        IConnectionAuthorizationFacade? connectionAuthorization = null
    )
    {
        _httpClientFactory = httpClientFactory;
        _serializer = serializer;
        _connectionAuthorization = connectionAuthorization;
        _options = options.Value.HttpPoll;
    }

    public Task<IReadOnlyList<HttpPollItem>> FetchAsync(
        EventSource source,
        EventHttpPollSourceConfig config,
        CancellationToken cancellationToken
    ) =>
        FetchPagesAsync<HttpPollItem>(
            source,
            config,
            (_, body) =>
            {
                var root =
                    JsonNode.Parse(Encoding.UTF8.GetString(body))
                    ?? throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll response must contain a JSON value.");
                return (ExtractItems(root, config), root);
            },
            cancellationToken
        );

    public Task<IReadOnlyList<CloudEventEnvelope>> FetchCloudEventsAsync(
        EventSource source,
        EventHttpPollSourceConfig config,
        CancellationToken cancellationToken
    ) =>
        FetchPagesAsync<CloudEventEnvelope>(
            source,
            config,
            (response, body) =>
            {
                var contentType = response.Content.Headers.ContentType?.MediaType;
                var supportedContentType =
                    string.Equals(
                        contentType,
                        ICloudEventEnvelopeSerializer.BatchContentType,
                        StringComparison.OrdinalIgnoreCase
                    ) || string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase);
                if (!supportedContentType)
                    throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll CloudEvents content type is invalid.");
                return (_serializer.DeserializeBatch(body, contentType), null);
            },
            cancellationToken
        );

    private async Task<IReadOnlyList<T>> FetchPagesAsync<T>(
        EventSource source,
        EventHttpPollSourceConfig config,
        Func<HttpResponseMessage, byte[], (IReadOnlyList<T> Items, JsonNode? Root)> parsePage,
        CancellationToken cancellationToken
    )
    {
        if (_options.MaxResponseBytes <= 0 || _options.MaxItems <= 0 || _options.MaxPages <= 0)
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll limits must be positive.");
        var maxPages = config.Pagination.MaxPages ?? _options.MaxPages;
        if (maxPages <= 0)
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll page limit must be positive.");

        var startUri = new Uri(config.Request.Url, UriKind.Absolute);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var context = new PageFetchContext<T>
        {
            Source = source,
            Config = config,
            Client = client,
            ParsePage = parsePage,
        };
        var state = new PageState
        {
            StartUri = startUri,
            CurrentUri = startUri,
            Number = 1,
        };
        var items = new List<T>();
        while (true)
        {
            var page = await FetchPageAsync(context, state, items.Count, cancellationToken);
            items.AddRange(page.Items);
            if (page.NextUri is null)
                return items;
            if (state.Number >= maxPages)
                throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll page limit was exceeded.");
            state = state with { CurrentUri = page.NextUri, Cursor = page.Cursor, Number = state.Number + 1 };
        }
    }

    private sealed record PageState
    {
        public required Uri StartUri { get; init; }

        public required Uri CurrentUri { get; init; }

        public string? Cursor { get; init; }

        public required int Number { get; init; }
    }

    private sealed class PageFetchContext<T>
    {
        public required EventSource Source { get; init; }

        public required EventHttpPollSourceConfig Config { get; init; }

        public required HttpClient Client { get; init; }

        public required Func<
            HttpResponseMessage,
            byte[],
            (IReadOnlyList<T> Items, JsonNode? Root)
        > ParsePage { get; init; }
    }

    private async Task<(IReadOnlyList<T> Items, Uri? NextUri, string? Cursor)> FetchPageAsync<T>(
        PageFetchContext<T> context,
        PageState state,
        int itemCount,
        CancellationToken cancellationToken
    )
    {
        using var request = await CreateRequestAsync(context.Source, context.Config, state, cancellationToken);
        using var response = await context.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
        var page = context.ParsePage(response, await ReadBodyBytesAsync(response, cancellationToken));
        if (itemCount + page.Items.Count > _options.MaxItems)
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll item limit was exceeded.");
        var next = NextPage(context.Config, state, response, page.Root, page.Items.Count);
        return (page.Items, next.Uri, next.Cursor);
    }

    private static (Uri? Uri, string? Cursor) NextPage(
        EventHttpPollSourceConfig config,
        PageState state,
        HttpResponseMessage response,
        JsonNode? root,
        int itemCount
    )
    {
        switch (config.Pagination.Kind)
        {
            case EventHttpPollPaginationConfig.None:
                return (null, null);
            case EventHttpPollPaginationConfig.LinkHeader:
                var linkUrl = response.Headers.EnumerateLinkHeaders().GetLinkUrl("next");
                if (linkUrl is null)
                    return (null, null);
                var nextUri = new Uri(state.CurrentUri, linkUrl);
                ValidateSameOrigin(state.StartUri, nextUri);
                return (nextUri, null);
            case EventHttpPollPaginationConfig.Page:
                return itemCount > 0 ? (state.StartUri, null) : (null, null);
            case EventHttpPollPaginationConfig.Cursor when root is not null:
                var cursor = ReadCursor(root, config.Pagination.CursorPath!);
                return string.IsNullOrEmpty(cursor) ? (null, null) : (state.StartUri, cursor);
            default:
                throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll pagination is invalid.");
        }
    }

    public static string CloudEventItemKey(string source, string id) => JsonSerializer.Serialize(new[] { source, id });

    public static CloudEventDraft CreateEventDraft(
        EventSource source,
        EventHttpPollSourceConfig config,
        HttpPollItemChange change
    )
    {
        var item = JsonNode.Parse(change.Data.GetRawText()) ?? throw new AgwException(ErrorCodes.InvalidParam);
        var context = new JsonObject { ["item"] = item.DeepClone() };
        var mapping = config.Mapping!;
        var eventType =
            config.FixedType
            ?? (change.Kind == PollItemChangeKind.New ? mapping.Event.TypeOnNew : mapping.Event.TypeOnChange)!;
        var jsonData = JsonSerializer.SerializeToElement(item);
        return new CloudEventDraft
        {
            Id = $"{change.ItemKey}:{change.ChangeSeq.ToString(CultureInfo.InvariantCulture)}",
            Type = eventType,
            Source = source.CeSource,
            Subject = ReadOptionalString(mapping.Event.Subject, context),
            Time = ReadOptionalTime(mapping.Event.Time, context),
            DataContentType = "application/json",
            DataSchema = mapping.Event.DataSchema,
            Data = new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = jsonData.GetRawText(),
                Json = jsonData,
            },
            Scope = mapping.Event.Scope,
            Tags = ReadTags(mapping.Event.Tags, context),
            Summary = ReadOptionalString(mapping.Event.Summary, context),
            Link = ReadOptionalString(mapping.Event.Link, context),
        };
    }

    public static SocketsHttpHandler CreateHandler(EventHttpPollOptions options) =>
        new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                if (addresses.Length == 0 || !options.AllowPrivateNetworks && addresses.Any(IsPrivateAddress))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        "HTTP poll target resolves to a prohibited address."
                    );
                }

                SocketException? lastError = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException exception)
                    {
                        socket.Dispose();
                        lastError = exception;
                    }
                }

                throw lastError ?? new SocketException((int)SocketError.HostNotFound);
            },
        };

    private async Task<HttpRequestMessage> CreateRequestAsync(
        EventSource source,
        EventHttpPollSourceConfig config,
        PageState state,
        CancellationToken cancellationToken
    )
    {
        var request = new HttpRequestMessage(
            new HttpMethod(config.Request.Method),
            CreatePageUri(config.Pagination, state)
        );
        var complete = false;
        try
        {
            AddRequestHeaders(request, config.Request.Headers);
            if (config.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                request.Content = new StringContent(
                    config.Request.Body?.GetRawText() ?? string.Empty,
                    Encoding.UTF8,
                    "application/json"
                );

            await ApplyAuthenticationAsync(request, config.Request.Auth, source, cancellationToken);
            complete = true;
            return request;
        }
        finally
        {
            if (!complete)
                request.Dispose();
        }
    }

    private static Uri CreatePageUri(EventHttpPollPaginationConfig pagination, PageState state) =>
        pagination.Kind switch
        {
            EventHttpPollPaginationConfig.Cursor when state.Cursor is { } cursor => new Uri(
                QueryHelpers.AddQueryString(state.CurrentUri.ToString(), pagination.CursorParameter!, cursor)
            ),
            EventHttpPollPaginationConfig.Page => new Uri(
                QueryHelpers.AddQueryString(
                    state.CurrentUri.ToString(),
                    pagination.PageParameter ?? "page",
                    state.Number.ToString(CultureInfo.InvariantCulture)
                )
            ),
            _ => state.CurrentUri,
        };

    private static void AddRequestHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            var invalidHeader =
                name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || name.Any(char.IsControl)
                || value.Any(char.IsControl);
            if (invalidHeader || !request.Headers.TryAddWithoutValidation(name, value))
                throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll request header is invalid.");
        }

        if (request.Headers.Accept.Count == 0)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private async Task ApplyAuthenticationAsync(
        HttpRequestMessage request,
        EventHttpPollAuthConfig auth,
        EventSource source,
        CancellationToken cancellationToken
    )
    {
        switch (auth.Kind)
        {
            case "none":
                return;
            case "bearer":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReadSecretString(source));
                return;
            case "basic":
                var basic = ParseSecretObject(source);
                var credentials = $"{ReadStringProperty(basic, "username")}:{ReadStringProperty(basic, "password")}";
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials))
                );
                return;
            case "header":
                AddAuthenticationHeader(
                    request,
                    auth.HeaderName!,
                    ReadStringProperty(ParseSecretObject(source), "value")
                );
                return;
            case "connection":
                request.Headers.Authorization = await ReadConnectionAuthorizationAsync(
                    auth.ConnectionId!.Value,
                    cancellationToken
                );
                return;
            default:
                throw new AgwException(ErrorCodes.InvalidParam);
        }
    }

    private static void AddAuthenticationHeader(HttpRequestMessage request, string name, string value)
    {
        var reserved =
            name.Equals("Host", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);
        if (reserved || !request.Headers.TryAddWithoutValidation(name, value))
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll authentication header is invalid.");
    }

    private async Task<AuthenticationHeaderValue> ReadConnectionAuthorizationAsync(
        Guid connectionId,
        CancellationToken cancellationToken
    )
    {
        var connectionAuthorization =
            _connectionAuthorization ?? throw new AgwException(ErrorCodes.IntegrationCredentialUnavailable);
        var authorization = await connectionAuthorization.GetAuthorizationHeaderAsync(connectionId, cancellationToken);
        if (!AuthenticationHeaderValue.TryParse(authorization, out var parsedAuthorization))
            throw new AgwException(ErrorCodes.InvalidParam, "Connection authorization header is invalid.");
        return parsedAuthorization;
    }

    private async Task<byte[]> ReadBodyBytesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > _options.MaxResponseBytes)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll response exceeded the configured size limit.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await responseStream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > _options.MaxResponseBytes)
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "HTTP poll response exceeded the configured size limit."
                );
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return output.ToArray();
    }

    private static List<HttpPollItem> ExtractItems(JsonNode root, EventHttpPollSourceConfig config)
    {
        var path = JsonPath.Parse(config.Mapping!.Items);
        var matches = path.Evaluate(root).Matches;
        var result = new List<HttpPollItem>();
        foreach (var match in matches ?? [])
        {
            var value = match.Value;
            if (value is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        result.Add(CreateItem(item, config));
                    }
                }
            }
            else if (value is not null)
            {
                result.Add(CreateItem(value, config));
            }
        }

        return result;
    }

    private static HttpPollItem CreateItem(JsonNode item, EventHttpPollSourceConfig config)
    {
        var context = new JsonObject { ["item"] = item.DeepClone() };
        var keyNode = Apply(config.Mapping!.Key, context);
        var key =
            ReadScalar(keyNode)
            ?? throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll item key must be a scalar value.");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll item key must not be empty.");
        }

        var fingerprint =
            config.Mapping.Change.Kind == "content"
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.ToJsonString())))
                : JsonLogicValue(config.Mapping.Change.Value!.Value, context);
        return new HttpPollItem(key, fingerprint, JsonSerializer.SerializeToElement(item));
    }

    private static string ReadCursor(JsonNode root, string path)
    {
        var cursor = JsonPath.Parse(path).Evaluate(root).Matches?.FirstOrDefault()?.Value;
        return ReadScalar(cursor) ?? string.Empty;
    }

    private static string JsonLogicValue(JsonElement rule, JsonNode context)
    {
        var result = Apply(rule, context);
        return result?.ToJsonString() ?? "null";
    }

    private static JsonNode? Apply(JsonElement rule, JsonNode context)
    {
        var jsonRule = JsonNode.Parse(rule.GetRawText()) ?? throw new AgwException(ErrorCodes.InvalidParam);
        return JsonLogic.Apply(jsonRule, context.DeepClone());
    }

    private static string? ReadOptionalString(JsonElement? rule, JsonNode context) =>
        rule is { } value ? ReadScalar(Apply(value, context)) : null;

    private static IReadOnlyList<string>? ReadTags(JsonElement? rule, JsonNode context)
    {
        if (rule is not { } value)
        {
            return null;
        }

        var result = Apply(value, context);
        if (result is not JsonArray tags)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll event tags expression must return an array.");
        }

        var output = new List<string>(tags.Count);
        foreach (var tag in tags)
        {
            var text = ReadScalar(tag);
            if (text is null)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll event tags must be strings.");
            }
            output.Add(text);
        }

        return output;
    }

    private static DateTimeOffset? ReadOptionalTime(JsonElement? rule, JsonNode context)
    {
        var value = ReadOptionalString(rule, context);
        if (value is null)
        {
            return null;
        }

        var timeIndex = value.IndexOf('T', StringComparison.OrdinalIgnoreCase);
        var hasOffset = value.EndsWith('Z') || timeIndex >= 0 && value.AsSpan(timeIndex + 1).IndexOfAny('+', '-') >= 0;
        if (
            !hasOffset
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll event time must be an RFC 3339 timestamp.");
        }

        return time;
    }

    private static string? ReadScalar(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(value.ToJsonString());
        var root = document.RootElement;
        return root.ValueKind switch
        {
            JsonValueKind.String => root.GetString(),
            JsonValueKind.Number => root.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string ReadSecretString(EventSource source)
    {
        using var document = JsonDocument.Parse(source.SecretJson ?? throw new AgwException(ErrorCodes.InvalidParam));
        var value = document.RootElement.GetString();
        return string.IsNullOrEmpty(value) ? throw new AgwException(ErrorCodes.InvalidParam) : value;
    }

    private static JsonElement ParseSecretObject(EventSource source)
    {
        using var document = JsonDocument.Parse(source.SecretJson ?? throw new AgwException(ErrorCodes.InvalidParam));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        return document.RootElement.Clone();
    }

    private static string ReadStringProperty(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? throw new AgwException(ErrorCodes.InvalidParam)
            : throw new AgwException(ErrorCodes.InvalidParam);

    private static void ValidateSameOrigin(Uri startUri, Uri nextUri)
    {
        if (
            !startUri.Scheme.Equals(nextUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !startUri.Host.Equals(nextUri.Host, StringComparison.OrdinalIgnoreCase)
            || startUri.Port != nextUri.Port
            || !string.IsNullOrEmpty(nextUri.UserInfo)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll pagination must stay on the configured origin.");
        }
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 0
                || bytes[0] == 10
                || bytes[0] == 127
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
                || bytes[0] >= 224;
        }

        return address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast
            || (bytes[0] & 0xfe) == 0xfc;
    }
}
