using System.Security.Cryptography;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Events.Contracts;
using Agw.Extensions.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agw.Events.Application.Sources;

public sealed class EventSourceAppService
{
    private readonly IEventsDbContext _db;
    private readonly IEventScheduler _scheduler;
    private readonly IEventSourcePollService _pollService;
    private readonly IEventIngestTransaction _transaction;
    private readonly IExtensionGenerationCatalog _generationCatalog;
    private readonly EventsOptions _options;
    private readonly TimeProvider _timeProvider;

    public EventSourceAppService(
        IEventsDbContext db,
        IEventScheduler scheduler,
        IEventSourcePollService pollService,
        IEventIngestTransaction transaction,
        IExtensionGenerationCatalog generationCatalog,
        IOptions<EventsOptions> options,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _scheduler = scheduler;
        _pollService = pollService;
        _transaction = transaction;
        _generationCatalog = generationCatalog;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<EventSource>> ListAsync(CancellationToken cancellationToken)
    {
        var userId = UserInfoUtil.RequiredUserId;
        return await _db
            .EventSources.AsNoTracking()
            .Where(source => source.CreateBy == userId && source.DeletedAt == null)
            .OrderBy(source => source.Name)
            .ThenBy(source => source.Id)
            .ToArrayAsync(cancellationToken);
    }

    public Task<EventSource?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var userId = UserInfoUtil.RequiredUserId;
        return _db
            .EventSources.AsNoTracking()
            .SingleOrDefaultAsync(
                source => source.Id == id && source.CreateBy == userId && source.DeletedAt == null,
                cancellationToken
            );
    }

    public async Task<EventSourceMutationResult> CreateAsync(
        EventSourceCreateCommand request,
        CancellationToken cancellationToken
    )
    {
        var userId = UserInfoUtil.RequiredUserId;
        ValidateName(request.Name);
        ValidateKind(request.Kind);
        ValidateProvider(request.Kind, request.ProviderExtensionId, request.ProviderId, request.Config);
        var secret = SecretJson(request.Secret);
        ValidateConfiguration(request.Kind, request.Config, secret is null ? null : ParseSecret(secret));

        var now = _timeProvider.GetUtcNow();
        var sourceId = Guid.CreateVersion7();
        var source = new EventSource
        {
            Id = sourceId,
            CreateBy = userId,
            Name = request.Name.Trim(),
            Kind = request.Kind,
            ProviderExtensionId = request.ProviderExtensionId,
            ProviderId = request.ProviderId,
            CeSource = $"/agw/sources/{sourceId:N}",
            ConfigJson = request.Config.GetRawText(),
            SecretJson = secret,
            Enabled = request.Enabled,
            NextRunAt =
                request.Enabled && request.Kind is (EventSourceKind.HttpPoll or EventSourceKind.Provider)
                    ? NextRunAt(request.Kind, request.Config, now)
                    : null,
        };

        try
        {
            await _transaction.ExecuteAsync(
                async token =>
                {
                    _db.EventSources.Add(source);
                    if (source.Kind == EventSourceKind.Timer && source.Enabled)
                    {
                        await SyncTimerAsync(source, requestSeq: 1, token);
                    }
                },
                cancellationToken
            );
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw ModificationConflict(exception);
        }

        return Mutation(source);
    }

    public async Task<EventSourceMutationResult> UpdateAsync(
        EventSourceUpdateCommand request,
        CancellationToken cancellationToken
    )
    {
        ValidateName(request.Name);
        var source = await FindManagedAsync(request.Id, cancellationToken);
        EnsureEditable(source);
        EnsureVersion(source, request.Version);

        var retainedSecret = request.Secret is null ? source.SecretJson : SecretJson(request.Secret);
        if (source.Kind == EventSourceKind.HttpPoll && request.Secret is null)
        {
            var config = EventSourceConfigParser.ParseHttpPoll(request.Config, _options.HttpPoll);
            if (config.Request.Auth.Kind is "none" or "connection")
            {
                retainedSecret = null;
            }
        }
        if (source.Kind is EventSourceKind.Timer or EventSourceKind.Provider)
        {
            retainedSecret = null;
        }

        ValidateProvider(source.Kind, source.ProviderExtensionId, source.ProviderId, request.Config);
        ValidateConfiguration(source.Kind, request.Config, retainedSecret is null ? null : ParseSecret(retainedSecret));

        source.Name = request.Name.Trim();
        source.ConfigJson = request.Config.GetRawText();
        source.SecretJson = retainedSecret;
        ResetPollState(source, request.Config);

        try
        {
            await _transaction.ExecuteAsync(
                async token =>
                {
                    if (source.Kind == EventSourceKind.Timer)
                    {
                        await SyncTimerAsync(source, request.Version + 1, token);
                    }
                },
                cancellationToken
            );
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw ModificationConflict(exception);
        }

        return Mutation(source);
    }

    public async Task<EventSourceMutationResult> SetEnabledAsync(
        EventSourceEnableCommand request,
        CancellationToken cancellationToken
    )
    {
        var source = await FindManagedAsync(request.Id, cancellationToken);
        EnsureEditable(source);
        EnsureVersion(source, request.Version);
        source.Enabled = request.Enabled;
        if (request.Enabled && source.Kind is (EventSourceKind.HttpPoll or EventSourceKind.Provider))
        {
            using var configDocument = JsonDocument.Parse(source.ConfigJson);
            source.NextRunAt = NextRunAt(source.Kind, configDocument.RootElement, _timeProvider.GetUtcNow());
        }
        else
        {
            source.NextRunAt = null;
        }

        try
        {
            await _transaction.ExecuteAsync(
                async token =>
                {
                    if (source.Kind == EventSourceKind.Timer)
                    {
                        await SyncTimerAsync(source, request.Version + 1, token);
                    }
                },
                cancellationToken
            );
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw ModificationConflict(exception);
        }

        return Mutation(source);
    }

    public async Task DeleteAsync(EventSourceVersionCommand request, CancellationToken cancellationToken)
    {
        var source = await FindManagedAsync(request.Id, cancellationToken);
        EnsureEditable(source);
        EnsureVersion(source, request.Version);
        source.Enabled = false;
        source.NextRunAt = null;
        source.DeletedAt = _timeProvider.GetUtcNow();

        try
        {
            await _transaction.ExecuteAsync(
                async token =>
                {
                    if (source.Kind == EventSourceKind.Timer)
                    {
                        await _scheduler.CancelAsync($"source:{source.Id}", request.Version + 1, token);
                    }
                },
                cancellationToken
            );
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw ModificationConflict(exception);
        }
    }

    public async Task<EventSourceMutationResult> RotateSecretAsync(
        EventSourceVersionCommand request,
        CancellationToken cancellationToken
    )
    {
        var source = await FindManagedAsync(request.Id, cancellationToken);
        EnsureEditable(source);
        EnsureVersion(source, request.Version);
        if (source.Kind != EventSourceKind.Webhook)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Only webhook sources have a rotatable secret.");
        }

        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        source.SecretJson = JsonSerializer.Serialize(secret);

        try
        {
            await _transaction.ExecuteAsync(_ => Task.CompletedTask, cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw ModificationConflict(exception);
        }

        return Mutation(source) with
        {
            Secret = secret,
        };
    }

    public async Task<EventSourcePollTestResult> TestAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await FindManagedAsync(id, cancellationToken);
        if (source.Kind is not (EventSourceKind.HttpPoll or EventSourceKind.Provider))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Only polling sources can be tested.");
        }

        return await _pollService.TestAsync(source, cancellationToken);
    }

    private async Task<EventSource> FindManagedAsync(Guid id, CancellationToken cancellationToken)
    {
        var userId = UserInfoUtil.RequiredUserId;
        return await _db.EventSources.SingleOrDefaultAsync(
                source => source.Id == id && source.CreateBy == userId && source.DeletedAt == null,
                cancellationToken
            ) ?? throw new AgwException(ErrorCodes.EventSourceNotFound);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Event source name is required and must be at most 200 characters."
            );
        }
    }

    private static void ValidateKind(EventSourceKind kind)
    {
        if (kind is EventSourceKind.Publisher || !Enum.IsDefined(kind))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Publisher sources are managed by the event publisher.");
        }
    }

    private void ValidateProvider(EventSourceKind kind, Guid? extensionId, string? providerId, JsonElement config)
    {
        if (kind != EventSourceKind.Provider)
        {
            if (extensionId is not null || providerId is not null)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Only provider sources specify a provider extension.");
            }
            return;
        }

        if (
            extensionId is not { } id
            || string.IsNullOrWhiteSpace(providerId)
            || config.ValueKind != JsonValueKind.Object
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Provider source configuration is incomplete.");
        }

        if (
            !_generationCatalog
                .GetSnapshots()
                .Any(snapshot =>
                    snapshot.GenerationId.ExtensionId == id
                    && snapshot.State == ExtensionGenerationState.Active
                    && snapshot.EventSourceProviders.Any(provider => provider.Id == providerId)
                )
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The event source provider is unavailable.");
        }
    }

    private void ValidateConfiguration(EventSourceKind kind, JsonElement config, JsonElement? secret)
    {
        switch (kind)
        {
            case EventSourceKind.Webhook:
                var webhook = EventSourceConfigParser.ParseWebhook(config);
                EventSourceConfigParser.ValidateWebhookSecret(webhook, secret);
                break;
            case EventSourceKind.HttpPoll:
                var poll = EventSourceConfigParser.ParseHttpPoll(config, _options.HttpPoll);
                EventSourceConfigParser.ValidateHttpPollSecret(poll, secret);
                break;
            case EventSourceKind.Timer:
                _ = EventSourceConfigParser.ParseTimer(config);
                if (secret is not null)
                {
                    throw new AgwException(ErrorCodes.InvalidParam, "Timer sources do not accept a secret.");
                }
                break;
            case EventSourceKind.Provider:
                if (secret is not null)
                {
                    throw new AgwException(ErrorCodes.InvalidParam, "Provider sources do not accept a secret.");
                }
                break;
            default:
                throw new AgwException(ErrorCodes.InvalidParam);
        }
    }

    private void ResetPollState(EventSource source, JsonElement config)
    {
        if (source.Kind is EventSourceKind.HttpPoll or EventSourceKind.Provider)
        {
            source.BaselineAt = null;
            source.CursorJson = null;
            source.LastError = null;
            source.NextRunAt = source.Enabled ? NextRunAt(source.Kind, config, _timeProvider.GetUtcNow()) : null;
            _db.EventSourceItems.RemoveRange(_db.EventSourceItems.Where(item => item.SourceId == source.Id));
        }
    }

    private DateTimeOffset NextRunAt(EventSourceKind kind, JsonElement config, DateTimeOffset now)
    {
        if (kind == EventSourceKind.HttpPoll)
        {
            var poll = EventSourceConfigParser.ParseHttpPoll(config, _options.HttpPoll);
            return now + System.Xml.XmlConvert.ToTimeSpan(poll.Interval);
        }

        return now + _options.HttpPoll.MinInterval;
    }

    private async Task SyncTimerAsync(EventSource source, long requestSeq, CancellationToken cancellationToken)
    {
        if (!source.Enabled || source.DeletedAt is not null)
        {
            await _scheduler.CancelAsync($"source:{source.Id}", requestSeq, cancellationToken);
            return;
        }

        using var configDocument = JsonDocument.Parse(source.ConfigJson);
        var config = EventSourceConfigParser.ParseTimer(configDocument.RootElement);
        TimeSpan? interval = config.Interval is null ? null : System.Xml.XmlConvert.ToTimeSpan(config.Interval);
        var eventData = ParseTemplateData(config.Event);
        await _scheduler.UpsertAsync(
            new ScheduleRequest
            {
                BusinessKey = $"source:{source.Id}",
                RequestSeq = requestSeq,
                Kind = config.Kind switch
                {
                    "Once" => ScheduleKind.Once,
                    "Interval" => ScheduleKind.Interval,
                    "Cron" => ScheduleKind.Cron,
                    _ => throw new AgwException(ErrorCodes.InvalidParam),
                },
                At = config.Kind == "Once" ? config.At : null,
                Interval = interval,
                Cron = config.Cron,
                TimeZone = config.TimeZone,
                Event = new CloudEventDraft
                {
                    Id = "source-template",
                    Type = config.Event.Type,
                    Source = source.CeSource,
                    Time = config.Event.Time,
                    Subject = config.Event.Subject,
                    DataContentType = config.Event.DataContentType,
                    DataSchema = config.Event.DataSchema,
                    Data = eventData,
                    Scope = config.Event.Scope,
                    Tags = config.Event.Tags,
                    Summary = config.Event.Summary,
                    Link = config.Event.Link,
                },
            },
            cancellationToken
        );
    }

    private static CloudEventData? ParseTemplateData(EventSourceEventTemplate template)
    {
        if (template.DataBase64 is not null)
        {
            return new CloudEventData
            {
                Kind = CloudEventDataKind.Binary,
                Binary = Convert.FromBase64String(template.DataBase64),
            };
        }

        if (template.Data is { } data)
        {
            return new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = data.GetRawText(),
                Json = data.Clone(),
            };
        }

        return null;
    }

    private static JsonElement? ParseSecret(string secretJson)
    {
        using var document = JsonDocument.Parse(secretJson);
        return document.RootElement.Clone();
    }

    private static string? SecretJson(JsonElement? secret) =>
        secret is { } value && value.ValueKind != JsonValueKind.Null ? value.GetRawText() : null;

    private static EventSourceMutationResult Mutation(EventSource source) =>
        new() { Id = source.Id, Version = source.Version };

    private static AgwException ModificationConflict(DbUpdateConcurrencyException exception) =>
        new(ErrorCodes.EventSourceModificationConflict, "The event source changed after it was read.", exception);

    private static void EnsureEditable(EventSource source)
    {
        if (source.Kind == EventSourceKind.Publisher)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Publisher sources are managed by the event publisher.");
        }
    }

    private static void EnsureVersion(EventSource source, long version)
    {
        if (source.Version != version)
        {
            throw new AgwException(ErrorCodes.EventSourceModificationConflict);
        }
    }
}
