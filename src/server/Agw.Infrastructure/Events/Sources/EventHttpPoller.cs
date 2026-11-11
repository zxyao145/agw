using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Integrations.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Json.Logic;
using Json.Path;
using Meziantou.Framework.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Events.Sources;

public sealed class EventHttpPoller
{
    public const string HttpClientName = "Agw.Events.HttpPoll";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionAuthorizationFacade? _connectionAuthorization;
    private readonly EventHttpPollOptions _options;

    public EventHttpPoller(
        IHttpClientFactory httpClientFactory,
        IOptions<EventsOptions> options,
        IConnectionAuthorizationFacade? connectionAuthorization = null
    )
    {
        _httpClientFactory = httpClientFactory;
        _connectionAuthorization = connectionAuthorization;
        _options = options.Value.HttpPoll;
    }

    public async Task<IReadOnlyList<HttpPollItem>> FetchAsync(
        EventSource source,
        EventHttpPollSourceConfig config,
        CancellationToken cancellationToken
    )
    {
        if (_options.MaxResponseBytes <= 0 || _options.MaxItems <= 0 || _options.MaxPages <= 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll limits must be positive.");
        }

        var startUri = new Uri(config.Request.Url, UriKind.Absolute);
        var currentUri = startUri;
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var items = new List<HttpPollItem>();
        var pageNumber = 1;
        string? cursor = null;
        var hasNext = true;

        while (hasNext)
        {
            if (pageNumber > (config.Pagination.MaxPages ?? _options.MaxPages))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll page limit was exceeded.");
            }

            using var request = await CreateRequestAsync(
                source,
                config,
                currentUri,
                cursor,
                pageNumber,
                cancellationToken
            );
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            response.EnsureSuccessStatusCode();
            var body = await ReadBodyAsync(response, cancellationToken);
            var root =
                JsonNode.Parse(body)
                ?? throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll response must contain a JSON value.");
            var pageItems = ExtractItems(root, config);
            if (items.Count + pageItems.Count > _options.MaxItems)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll item limit was exceeded.");
            }

            items.AddRange(pageItems);
            pageNumber++;

            switch (config.Pagination.Kind)
            {
                case "none":
                    hasNext = false;
                    break;
                case "linkHeader":
                    var linkUrl = response.Headers.EnumerateLinkHeaders().GetLinkUrl("next");
                    hasNext = linkUrl is not null;
                    if (linkUrl is not null)
                    {
                        currentUri = new Uri(currentUri, linkUrl);
                        ValidateSameOrigin(startUri, currentUri);
                    }
                    break;
                case "cursor":
                    cursor = ReadCursor(root, config.Pagination.CursorPath!);
                    hasNext = !string.IsNullOrEmpty(cursor);
                    if (hasNext)
                    {
                        currentUri = startUri;
                    }
                    break;
                case "page":
                    hasNext = pageItems.Count > 0;
                    currentUri = startUri;
                    break;
                default:
                    throw new AgwException(ErrorCodes.InvalidParam);
            }

            if (hasNext && pageNumber - 1 >= (config.Pagination.MaxPages ?? _options.MaxPages))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll page limit was exceeded.");
            }
        }

        return items;
    }

    public static CloudEventDraft CreateEventDraft(
        EventSource source,
        EventHttpPollSourceConfig config,
        HttpPollItemChange change
    )
    {
        var item = JsonNode.Parse(change.Data.GetRawText()) ?? throw new AgwException(ErrorCodes.InvalidParam);
        var context = new JsonObject { ["item"] = item.DeepClone() };
        var eventType = change.Kind == PollItemChangeKind.New ? config.Event.TypeOnNew : config.Event.TypeOnChange;
        var jsonData = JsonSerializer.SerializeToElement(item);
        return new CloudEventDraft
        {
            Id = $"{change.ItemKey}:{change.ChangeSeq.ToString(CultureInfo.InvariantCulture)}",
            Type = eventType,
            Source = source.CeSource,
            Subject = ReadOptionalString(config.Event.Subject, context),
            Time = ReadOptionalTime(config.Event.Time, context),
            DataContentType = "application/json",
            DataSchema = config.Event.DataSchema,
            Data = new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = jsonData.GetRawText(),
                Json = jsonData,
            },
            Scope = config.Event.Scope,
            Tags = ReadTags(config.Event.Tags, context),
            Summary = ReadOptionalString(config.Event.Summary, context),
            Link = ReadOptionalString(config.Event.Link, context),
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
        Uri uri,
        string? cursor,
        int pageNumber,
        CancellationToken cancellationToken
    )
    {
        var requestUri = uri;
        if (config.Pagination.Kind == "cursor" && cursor is not null)
        {
            requestUri = new Uri(
                QueryHelpers.AddQueryString(uri.ToString(), config.Pagination.CursorParameter!, cursor)
            );
        }
        else if (config.Pagination.Kind == "page")
        {
            var pageParameter = config.Pagination.PageParameter ?? "page";
            requestUri = new Uri(
                QueryHelpers.AddQueryString(
                    uri.ToString(),
                    pageParameter,
                    pageNumber.ToString(CultureInfo.InvariantCulture)
                )
            );
        }

        var request = new HttpRequestMessage(new HttpMethod(config.Request.Method), requestUri);
        foreach (var (name, value) in config.Request.Headers)
        {
            if (
                name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || name.Any(char.IsControl)
                || value.Any(char.IsControl)
                || !request.Headers.TryAddWithoutValidation(name, value)
            )
            {
                request.Dispose();
                throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll request header is invalid.");
            }
        }

        if (!request.Headers.Accept.Any())
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        if (config.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            request.Content = config.Request.Body is { } body
                ? new StringContent(body.GetRawText(), Encoding.UTF8, "application/json")
                : new StringContent(string.Empty, Encoding.UTF8, "application/json");
        }

        switch (config.Request.Auth.Kind)
        {
            case "none":
                break;
            case "bearer":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReadSecretString(source));
                break;
            case "basic":
                var basic = ParseSecretObject(source);
                var credentials = $"{ReadStringProperty(basic, "username")}:{ReadStringProperty(basic, "password")}";
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials))
                );
                break;
            case "header":
                var headerSecret = ParseSecretObject(source);
                var headerName = config.Request.Auth.HeaderName!;
                var headerValue = ReadStringProperty(headerSecret, "value");
                if (
                    headerName.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                )
                {
                    request.Dispose();
                    throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll authentication header is invalid.");
                }
                if (!request.Headers.TryAddWithoutValidation(headerName, headerValue))
                {
                    request.Dispose();
                    throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll authentication header is invalid.");
                }
                break;
            case "connection":
                var authorization = await (
                    _connectionAuthorization ?? throw new AgwException(ErrorCodes.IntegrationCredentialUnavailable)
                ).GetAuthorizationHeaderAsync(config.Request.Auth.ConnectionId!.Value, cancellationToken);
                if (!AuthenticationHeaderValue.TryParse(authorization, out var parsedAuthorization))
                {
                    request.Dispose();
                    throw new AgwException(ErrorCodes.InvalidParam, "Connection authorization header is invalid.");
                }
                request.Headers.Authorization = parsedAuthorization;
                break;
            default:
                request.Dispose();
                throw new AgwException(ErrorCodes.InvalidParam);
        }

        return request;
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static List<HttpPollItem> ExtractItems(JsonNode root, EventHttpPollSourceConfig config)
    {
        var path = JsonPath.Parse(config.Items);
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
        var keyNode = Apply(config.Key, context);
        var key =
            ReadScalar(keyNode)
            ?? throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll item key must be a scalar value.");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "HTTP poll item key must not be empty.");
        }

        var fingerprint =
            config.Change.Kind == "content"
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.ToJsonString())))
                : JsonLogicValue(config.Change.Value!.Value, context);
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
