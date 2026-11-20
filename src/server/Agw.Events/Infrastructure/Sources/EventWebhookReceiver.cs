using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Application.Sources;
using Agw.Events.Configuration;
using Agw.Events.Contracts.Transport;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agw.Events.Infrastructure.Sources;

public sealed class EventWebhookReceiver : IEventWebhookReceiver
{
    private readonly IEventsDbContext _db;
    private readonly IEventWebhookSourceLookup _sourceLookup;
    private readonly EventStaging _events;
    private readonly ICloudEventEnvelopeSerializer _serializer;
    private readonly IEventIngestTransaction _transaction;
    private readonly TimeProvider _timeProvider;
    private readonly EventsOptions _options;

    public EventWebhookReceiver(
        IEventsDbContext db,
        IEventWebhookSourceLookup sourceLookup,
        EventStaging events,
        ICloudEventEnvelopeSerializer serializer,
        IEventIngestTransaction transaction,
        TimeProvider timeProvider,
        IOptions<EventsOptions> options
    )
    {
        _db = db;
        _sourceLookup = sourceLookup;
        _events = events;
        _serializer = serializer;
        _transaction = transaction;
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
            await _sourceLookup.FindEnabledAsync(sourceId, cancellationToken)
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
        await _transaction.AcquireLockAsync($"event-source:{sourceId}", cancellationToken);
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
            if (config.Format == EventSourceFormats.CloudEvents)
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
                    draft = CloudEventDraftMapper.FromEnvelope(message, config.CloudEvent);
                }
                finally
                {
                    context.Request.Body = originalBody;
                }
            }
            else
                draft = EventWebhookMapper.MapMapped(
                    source,
                    config.Mapping!,
                    RequestContext(context.Request, body),
                    config.FixedType
                );
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Webhook event is invalid.", exception);
        }
        var result = await _events.StageExternalAsync(source, draft, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return new EventWebhookReceiveResult(result.EventId, result.Duplicate, result.Discarded);
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
}
