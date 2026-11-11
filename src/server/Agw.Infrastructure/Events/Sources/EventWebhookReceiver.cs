using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Json.Logic;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Events.Sources;

public sealed class EventWebhookReceiver : IEventWebhookReceiver
{
    private readonly AgwDbContext _db;
    private readonly EventStaging _events;
    private readonly CloudEventEnvelopeSerializer _serializer;
    private readonly PendingTransportMessages _pending;
    private readonly IApplicationLock _applicationLock;
    private readonly TimeProvider _timeProvider;
    private readonly EventsOptions _options;

    public EventWebhookReceiver(
        AgwDbContext db,
        EventStaging events,
        CloudEventEnvelopeSerializer serializer,
        PendingTransportMessages pending,
        IApplicationLock applicationLock,
        TimeProvider timeProvider,
        IOptions<EventsOptions> options
    )
    {
        _db = db;
        _events = events;
        _serializer = serializer;
        _pending = pending;
        _applicationLock = applicationLock;
        _timeProvider = timeProvider;
        _options = options.Value;
    }

    public async Task<EventWebhookReceiveResult> ReceiveAsync(
        Guid sourceId,
        HttpContext context,
        CancellationToken cancellationToken
    )
    {
        var source =
            await _db
                .EventSources.IgnoreUserScope()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row =>
                        row.Id == sourceId
                        && row.Kind == EventSourceKind.Webhook
                        && row.Enabled
                        && row.DeletedAt == null,
                    cancellationToken
                )
            ?? throw new AgwException(ErrorCodes.EventSourceNotFound);
        var config = EventSourceConfigParser.ParseWebhook(JsonSerializer.Deserialize<JsonElement>(source.ConfigJson));
        ValidateContentType(context.Request, config);
        var body = await ReadBodyAsync(context.Request, cancellationToken);
        Authenticate(
            context.Request,
            body,
            config.Auth,
            EventSourceConfigParser.ParseSecret(source.SecretJson).GetString()!
        );

        using var user = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, source.CreateBy!)], "event-webhook")
            )
        );
        await _pending.AcquireAsync(_applicationLock, $"event-source:{sourceId}", cancellationToken);
        source =
            await _db.EventSources.SingleOrDefaultAsync(
                row =>
                    row.Id == sourceId && row.Kind == EventSourceKind.Webhook && row.Enabled && row.DeletedAt == null,
                cancellationToken
            ) ?? throw new AgwException(ErrorCodes.EventSourceNotFound);
        config = EventSourceConfigParser.ParseWebhook(JsonSerializer.Deserialize<JsonElement>(source.ConfigJson));
        ValidateContentType(context.Request, config);
        Authenticate(
            context.Request,
            body,
            config.Auth,
            EventSourceConfigParser.ParseSecret(source.SecretJson).GetString()!
        );
        CloudEventDraft draft;
        try
        {
            if (config.Format == "cloudevents")
            {
                if (
                    context.Request.Headers.Any(header =>
                        header.Key.StartsWith("ce-", StringComparison.OrdinalIgnoreCase) && header.Value.Count != 1
                    )
                )
                    throw new AgwException(ErrorCodes.InvalidParam);
                var originalBody = context.Request.Body;
                await using var bufferedBody = new MemoryStream(body, writable: false);
                context.Request.Body = bufferedBody;
                try
                {
                    var message = await _serializer.DeserializeHttpAsync(context.Request, cancellationToken);
                    draft = FromEnvelope(message, config.CloudEvent);
                }
                finally
                {
                    context.Request.Body = originalBody;
                }
            }
            else
                draft = EventWebhookMapper.MapMapped(source, config.Mapping!, RequestContext(context.Request, body));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Webhook event is invalid.", exception);
        }
        if (draft.Type.StartsWith("agw.", StringComparison.Ordinal))
            throw new AgwException(ErrorCodes.EventTypeReserved);
        var result = await _events.StageSourceAsync(source, draft, null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return new EventWebhookReceiveResult(result.EventId, result.Duplicate);
    }

    private async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var maximum = checked((long)_options.Payload.MaxBytes * 2 + 65536);
        if (request.ContentLength > maximum)
            throw new AgwException(ErrorCodes.EventPayloadTooLarge);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await request.Body.ReadAsync(buffer, cancellationToken);
            if (count == 0)
                return output.ToArray();
            if (output.Length + count > maximum)
                throw new AgwException(ErrorCodes.EventPayloadTooLarge);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    private void Authenticate(HttpRequest request, byte[] body, EventWebhookAuthConfig auth, string secret)
    {
        var valid = auth.Kind switch
        {
            "HmacSha256" => WebhookSignatureVerifier.VerifyHmac(
                body,
                SingleHeader(request, auth.SignatureHeader ?? "X-Hub-Signature-256"),
                secret
            ),
            "BearerToken" => VerifyBearer(SingleHeader(request, auth.TokenHeader ?? "Authorization"), secret),
            _ => false,
        };
        if (!valid)
            throw new AgwException(ErrorCodes.WebhookAuthenticationFailed);
        if (auth.TimestampHeader is { } timestampHeader)
        {
            var value = SingleHeader(request, timestampHeader);
            DateTimeOffset timestamp;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                try
                {
                    timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds);
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    throw new AgwException(
                        ErrorCodes.WebhookAuthenticationFailed,
                        "Webhook timestamp is invalid.",
                        exception
                    );
                }
            }
            else if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
                throw new AgwException(ErrorCodes.WebhookAuthenticationFailed);
            if (
                (_timeProvider.GetUtcNow() - timestamp).Duration()
                > TimeSpan.FromSeconds(auth.MaxClockSkewSeconds ?? 300)
            )
                throw new AgwException(ErrorCodes.WebhookAuthenticationFailed);
        }
    }

    private static string SingleHeader(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var value) && value.Count == 1 ? value[0] ?? string.Empty : string.Empty;

    private static bool VerifyBearer(string value, string secret) =>
        AuthenticationHeaderValue.TryParse(value, out var header)
        && header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
        && header.Parameter is { } token
        && WebhookSignatureVerifier.VerifyToken(token, secret);

    private static void ValidateContentType(HttpRequest request, EventWebhookSourceConfig config)
    {
        if (
            !MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !config.ContentTypes.Any(allowed =>
                string.Equals(
                    MediaTypeHeaderValue.Parse(allowed).MediaType,
                    contentType.MediaType,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
            throw new AgwException(ErrorCodes.InvalidParam, "Webhook content type is not allowed.");
    }

    private static WebhookRequestContext RequestContext(HttpRequest request, byte[] body)
    {
        var contentType = MediaTypeHeaderValue.Parse(request.ContentType!);
        var mediaType = contentType.MediaType!;
        var isJson =
            mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
        var isText =
            isJson
            || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
        var encoding = string.IsNullOrWhiteSpace(contentType.CharSet)
            ? new UTF8Encoding(false, true)
            : Encoding.GetEncoding(contentType.CharSet.Trim('"'));
        var text = isText ? encoding.GetString(body) : null;
        return new WebhookRequestContext
        {
            Headers = request.Headers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToString(),
                StringComparer.OrdinalIgnoreCase
            ),
            BodyJson = isJson ? JsonSerializer.Deserialize<JsonElement>(text!) : null,
            BodyText = text,
            BodyBinary = isText ? null : body,
            DataContentType = request.ContentType,
        };
    }

    private static CloudEventDraft FromEnvelope(CloudEventEnvelope message, EventWebhookDisplayMappingConfig? display)
    {
        var tags = message.Extensions.TryGetValue("tags", out var tagValue)
            ? EventTagCodec.Decode(tagValue.Value)
            : null;
        var scope = message.Extensions.TryGetValue("scope", out var scopeValue) ? scopeValue.Value : null;
        var data = message.Data?.Kind switch
        {
            CloudEventDataKind.Json => message.Data.Json,
            CloudEventDataKind.Text => JsonSerializer.SerializeToElement(message.Data.Text),
            _ => (JsonElement?)null,
        };
        var attributes = JsonSerializer.SerializeToNode(
            new
            {
                source = message.Source,
                id = message.Id,
                type = message.Type,
                subject = message.Subject,
                time = message.Time,
                datacontenttype = message.DataContentType,
                dataschema = message.DataSchema,
                scope,
                tags,
                data,
            }
        );
        return new CloudEventDraft
        {
            Id = message.Id,
            Source = message.Source,
            Type = message.Type,
            Time = message.Time,
            Subject = message.Subject,
            DataContentType = message.DataContentType,
            DataSchema = message.DataSchema,
            Data = message.Data,
            Scope = scope,
            Tags = tags,
            Summary = Display(display?.Summary, attributes),
            Link = Display(display?.Link, attributes),
        };
    }

    private static string? Display(JsonElement? rule, JsonNode? attributes)
    {
        if (!rule.HasValue)
            return null;
        var value = JsonLogic.Apply(JsonNode.Parse(rule.Value.GetRawText())!, attributes?.DeepClone());
        return value is null ? null : value.GetValue<string>();
    }
}
