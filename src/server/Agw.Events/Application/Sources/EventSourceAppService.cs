using System.Security.Cryptography;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Events.Domain.Services;
using Agw.Extensions.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agw.Events.Application.Sources;

public sealed class EventSourceAppService
{
    private readonly IEventsDbContext _db;
    private readonly EventTimerScheduleSync _timerSchedule;
    private readonly IEventSourcePollService _pollService;
    private readonly IEventIngestTransaction _transaction;
    private readonly IExtensionGenerationCatalog _generationCatalog;
    private readonly EventsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IApplicationLock _applicationLock;
    private readonly EventSourceNameDomainService _nameService;
    private readonly IEventPublisherSources _publisherSources;

    public EventSourceAppService(
        IEventsDbContext db,
        EventTimerScheduleSync timerSchedule,
        IEventSourcePollService pollService,
        IEventIngestTransaction transaction,
        IExtensionGenerationCatalog generationCatalog,
        IOptions<EventsOptions> options,
        TimeProvider timeProvider,
        IApplicationLock applicationLock,
        EventSourceNameDomainService nameService,
        IEventPublisherSources publisherSources
    )
    {
        _db = db;
        _timerSchedule = timerSchedule;
        _pollService = pollService;
        _transaction = transaction;
        _generationCatalog = generationCatalog;
        _options = options.Value;
        _timeProvider = timeProvider;
        _applicationLock = applicationLock;
        _nameService = nameService;
        _publisherSources = publisherSources;
    }

    public async Task<PagedResult<EventSource>> ListAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        await _publisherSources.EnsureAllBuiltInAsync(cancellationToken);
        return await EventPaging.ToPagedResultAsync(
            QuerySources(),
            pageIndex,
            pageSize,
            static source => source,
            cancellationToken
        );
    }

    public async Task<IReadOnlyList<EventSourceOption>> ListOptionsAsync(CancellationToken cancellationToken)
    {
        await _publisherSources.EnsureAllBuiltInAsync(cancellationToken);
        var sources = await QuerySources().ToArrayAsync(cancellationToken);
        return sources
            .Select(source => new EventSourceOption
            {
                Id = source.Id,
                Name = source.Name,
                Kind = source.Kind,
                ManagedBy = source.ManagedBy,
                EventTypes = GetEventTypes(source),
            })
            .ToArray();
    }

    public IReadOnlyList<string> GetEventTypes(EventSource source)
    {
        var hasExtension = source.Kind == EventSourceKind.Provider || EventManagedBy.IsExtension(source.ManagedBy);
        var extension = hasExtension
            ? _generationCatalog
                .GetActiveGenerations()
                .SingleOrDefault(snapshot =>
                    source.Kind == EventSourceKind.Provider
                        ? snapshot.GenerationId.ExtensionId == source.ProviderExtensionId
                        : source.ManagedBy == EventManagedBy.ForExtension(snapshot.GenerationId.ExtensionId)
                )
            : null;
        if (hasExtension && extension is null)
            return [];
        if (source.Kind == EventSourceKind.Provider)
            return extension.FindSourceProvider(source.ProviderId)?.EventTypes ?? [];
        return EventSourceTypeDeclaration.Resolve(source, extension);
    }

    public IReadOnlyList<EventSourceProviderOption> ListProviders() =>
        _generationCatalog
            .GetActiveGenerations()
            .SelectMany(snapshot =>
                snapshot.EventSourceProviders.Select(provider => new EventSourceProviderOption
                {
                    ExtensionId = snapshot.GenerationId.ExtensionId,
                    ExtensionName = snapshot.Name,
                    ExtensionDisplayName = snapshot.DisplayName,
                    ProviderId = provider.Id,
                    EventTypes = provider.EventTypes,
                    Configurations = provider
                        .Configurations.Select(configuration => new EventSourceProviderConfigurationOption
                        {
                            Id = configuration.Id,
                            Name = configuration.Name,
                            Description = configuration.Description,
                        })
                        .ToArray(),
                })
            )
            .ToArray();

    private IOrderedQueryable<EventSource> QuerySources()
    {
        var userId = UserInfoUtil.RequiredUserId;
        return _db
            .EventSources.AsNoTracking()
            .Where(source => source.CreateBy == userId && source.DeletedAt == null)
            .OrderBy(source => source.Name)
            .ThenBy(source => source.Id);
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
        ValidateKind(request.Kind);
        EventSourceNames.Validate(request.Name, request.Kind, EventManagedBy.User);
        var provider = ValidateProvider(request.Kind, request.ProviderExtensionId, request.ProviderId, request.Config);
        if (request.Kind == EventSourceKind.Webhook && request.Secret is not null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Webhook secrets are generated by the server.");
        }
        var generatedSecret = request.Kind == EventSourceKind.Webhook ? GenerateWebhookSecret() : null;
        var secret = generatedSecret is null ? SecretJson(request.Secret) : JsonSerializer.Serialize(generatedSecret);
        var configuration = NormalizeConfiguration(request.Kind, request.Config, secret, provider);

        await using var nameLease = await _applicationLock.AcquireAsync(
            EventSourceNames.LockKey(userId),
            cancellationToken
        );
        await _nameService.EnsureAvailableAsync(request.Name, null, cancellationToken);

        var source = CreateSourceRecord(request, userId, configuration);
        await SaveCreatedSourceAsync(source, cancellationToken);
        nameLease.HandleLostToken.ThrowIfCancellationRequested();

        return Mutation(source) with
        {
            Secret = generatedSecret,
        };
    }

    private EventSource CreateSourceRecord(
        EventSourceCreateCommand request,
        string userId,
        SourceConfiguration configuration
    )
    {
        var sourceId = Guid.CreateVersion7();
        var source = new EventSource
        {
            Id = sourceId,
            CreateBy = userId,
            Name = request.Name,
            Kind = request.Kind,
            ProviderExtensionId = request.ProviderExtensionId,
            ProviderId = request.ProviderId,
            CeSource = EventSourceNames.CeSource(sourceId),
            ConfigJson = configuration.ConfigJson,
            SecretJson = configuration.SecretJson,
            Enabled = request.Enabled,
            NextRunAt =
                request.Enabled && configuration.PollInterval is { } interval
                    ? _timeProvider.GetUtcNow() + interval
                    : null,
        };
        return source;
    }

    private async Task SaveCreatedSourceAsync(EventSource source, CancellationToken cancellationToken)
    {
        try
        {
            await _transaction.ExecuteAsync(
                async token =>
                {
                    _db.EventSources.Add(source);
                    if (source.Kind == EventSourceKind.Timer && source.Enabled)
                    {
                        await _timerSchedule.SyncAsync(source, requestSeq: 1, token);
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

    public async Task<EventSourceMutationResult> UpdateAsync(
        EventSourceUpdateCommand request,
        CancellationToken cancellationToken
    )
    {
        await using var nameLease = await _applicationLock.AcquireAsync(
            EventSourceNames.LockKey(UserInfoUtil.RequiredUserId),
            cancellationToken
        );
        var source = await FindManagedAsync(request.Id, cancellationToken);
        EnsureEditable(source);
        EnsureVersion(source, request.Version);
        EventSourceNames.Validate(request.Name, source.Kind, source.ManagedBy);
        await _nameService.EnsureAvailableAsync(request.Name, source.Id, cancellationToken);

        if (source.Kind == EventSourceKind.Webhook && request.Secret is not null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Use secret rotation to change a webhook secret.");
        }
        var retainedSecret = request.Secret is null ? source.SecretJson : SecretJson(request.Secret);
        if (source.Kind is EventSourceKind.Timer or EventSourceKind.Provider)
        {
            retainedSecret = null;
        }

        var provider = ValidateProvider(source.Kind, source.ProviderExtensionId, source.ProviderId, request.Config);
        var configuration = NormalizeConfiguration(
            source.Kind,
            request.Config,
            retainedSecret,
            provider,
            retainExistingSecret: request.Secret is null
        );

        source.Name = request.Name;
        source.ConfigJson = configuration.ConfigJson;
        source.SecretJson = configuration.SecretJson;
        ResetPollState(source, configuration.PollInterval);

        try
        {
            await _transaction.ExecuteAsync(
                async token =>
                {
                    if (source.Kind == EventSourceKind.Timer)
                    {
                        await _timerSchedule.SyncAsync(source, request.Version + 1, token);
                    }
                },
                cancellationToken
            );
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw ModificationConflict(exception);
        }

        nameLease.HandleLostToken.ThrowIfCancellationRequested();

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
            source.NextRunAt = _timeProvider.GetUtcNow() + ReadPollInterval(source.Kind, configDocument.RootElement);
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
                        await _timerSchedule.SyncAsync(source, request.Version + 1, token);
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
                        await _timerSchedule.SyncAsync(source, request.Version + 1, token);
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

        var secret = GenerateWebhookSecret();
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

    private static void ValidateKind(EventSourceKind kind)
    {
        if (kind is EventSourceKind.Publisher || !Enum.IsDefined(kind))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Publisher sources are managed by the event publisher.");
        }
    }

    private ExtensionEventSourceProviderDescriptor? ValidateProvider(
        EventSourceKind kind,
        Guid? extensionId,
        string? providerId,
        JsonElement config
    )
    {
        if (kind != EventSourceKind.Provider)
        {
            if (extensionId is not null || providerId is not null)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Only provider sources specify a provider extension.");
            }
            return null;
        }

        if (
            extensionId is not { } id
            || string.IsNullOrWhiteSpace(providerId)
            || config.ValueKind != JsonValueKind.Object
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Provider source configuration is incomplete.");
        }

        var provider = _generationCatalog
            .GetActiveGenerations()
            .SingleOrDefault(snapshot => snapshot.GenerationId.ExtensionId == id)
            .FindSourceProvider(providerId);
        if (provider is null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The event source provider is unavailable.");
        }

        return provider;
    }

    private sealed record SourceConfiguration(string ConfigJson, string? SecretJson, TimeSpan? PollInterval = null);

    private SourceConfiguration NormalizeConfiguration(
        EventSourceKind kind,
        JsonElement config,
        string? secretJson,
        ExtensionEventSourceProviderDescriptor? provider,
        bool retainExistingSecret = false
    )
    {
        switch (kind)
        {
            case EventSourceKind.Webhook:
                var webhook = EventSourceConfigParser.ParseWebhook(config);
                EventSourceConfigParser.ValidateWebhookSecret(webhook, ParseSecret(secretJson));
                return new SourceConfiguration(EventSourceConfigParser.Serialize(webhook), secretJson);
            case EventSourceKind.HttpPoll:
                var poll = EventSourceConfigParser.ParseHttpPoll(config, _options.HttpPoll);
                if (retainExistingSecret && poll.Request.Auth.Kind is ("none" or "connection"))
                    secretJson = null;
                EventSourceConfigParser.ValidateHttpPollSecret(poll, ParseSecret(secretJson));
                return new SourceConfiguration(
                    EventSourceConfigParser.Serialize(poll),
                    secretJson,
                    System.Xml.XmlConvert.ToTimeSpan(poll.Interval)
                );
            case EventSourceKind.Timer:
                _ = EventSourceConfigParser.ParseTimer(config);
                if (secretJson is not null)
                {
                    throw new AgwException(ErrorCodes.InvalidParam, "Timer sources do not accept a secret.");
                }
                return new SourceConfiguration(config.GetRawText(), null);
            case EventSourceKind.Provider:
                if (secretJson is not null)
                {
                    throw new AgwException(ErrorCodes.InvalidParam, "Provider sources do not accept a secret.");
                }
                return new SourceConfiguration(
                    EventSourceConfigParser.Serialize(EventSourceConfigParser.ParseProvider(config, provider!)),
                    null,
                    _options.HttpPoll.MinInterval
                );
            default:
                throw new AgwException(ErrorCodes.InvalidParam);
        }
    }

    private void ResetPollState(EventSource source, TimeSpan? interval)
    {
        if (interval is { } pollInterval)
        {
            source.BaselineAt = null;
            source.CursorJson = null;
            source.LastError = null;
            source.NextRunAt = source.Enabled ? _timeProvider.GetUtcNow() + pollInterval : null;
            _db.EventSourceItems.RemoveRange(_db.EventSourceItems.Where(item => item.SourceId == source.Id));
        }
    }

    private TimeSpan ReadPollInterval(EventSourceKind kind, JsonElement config) =>
        kind == EventSourceKind.HttpPoll
            ? System.Xml.XmlConvert.ToTimeSpan(
                EventSourceConfigParser.ParseHttpPoll(config, _options.HttpPoll).Interval
            )
            : _options.HttpPoll.MinInterval;

    private static string GenerateWebhookSecret() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static JsonElement? ParseSecret(string? secretJson)
    {
        if (secretJson is null)
            return null;
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
        EventManagedRecordGuard.EnsureUserManaged(source.ManagedBy, ErrorCodes.EventSourceManaged, "Event source");
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
