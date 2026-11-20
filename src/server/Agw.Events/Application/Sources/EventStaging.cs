using Agw.Events.Application.Persistence;
using Agw.Events.Contracts;
using Agw.Events.Observability;
using Agw.Extensions.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;

namespace Agw.Events.Application.Sources;

public sealed record EventExternalStageResult(Guid? EventId, bool Duplicate, bool Discarded);

public sealed class EventStaging : IEventStaging
{
    private readonly IEventPublisherSources _publisherSources;
    private readonly IEventRecordStaging _records;
    private readonly IExtensionGenerationCatalog _extensions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventStaging> _logger;

    public EventStaging(
        IEventPublisherSources publisherSources,
        IEventRecordStaging records,
        IExtensionGenerationCatalog extensions,
        TimeProvider timeProvider,
        ILogger<EventStaging> logger
    )
    {
        _publisherSources = publisherSources;
        _records = records;
        _extensions = extensions;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<EventExternalStageResult> StageExternalAsync(
        EventSource source,
        CloudEventDraft draft,
        CancellationToken cancellationToken
    )
    {
        var extension =
            source.Kind == EventSourceKind.Provider
                ? _extensions
                    .GetActiveGenerations()
                    .SingleOrDefault(snapshot => snapshot.GenerationId.ExtensionId == source.ProviderExtensionId)
                    ?? throw new AgwException(ErrorCodes.EventSourceNotFound)
                : null;
        var declaration = EventSourceTypeDeclaration.Read(source, extension);
        if (declaration.FixedType is { } fixedType)
            draft = draft with { Type = fixedType };

        var declaredType =
            !EventTypePatterns.IsReserved(draft.Type)
            && EventTypePatterns.MatchesAny(declaration.EventTypes, draft.Type);
        if (!declaredType)
        {
            RecordDiscarded(source, draft.Id, draft.Type, EventDiscardReasons.UndeclaredType);
            return new EventExternalStageResult(null, false, true);
        }

        var staged = await _records.StageSourceAsync(source, draft, null, cancellationToken);
        return new EventExternalStageResult(staged.EventId, staged.Duplicate, false);
    }

    public void RecordDiscarded(
        EventSource source,
        string ceId,
        string eventType,
        string reason,
        Exception? exception = null
    )
    {
        source.LastDiscardedType =
            eventType.Length > EventSource.DiscardedTypeMaxLength
                ? eventType[..EventSource.DiscardedTypeMaxLength]
                : eventType;
        source.LastDiscardedAt = _timeProvider.GetUtcNow();
        EventTelemetry.Discarded.Add(1, new("source_kind", source.Kind.ToString()), new("reason", reason));
        _logger.LogWarning(
            exception,
            "Event {CeId} from source {SourceId} was discarded: {Reason}, type {EventType}",
            ceId,
            source.Id,
            reason,
            eventType
        );
    }

    public async Task<EventPublishResult> StageAsync(EventStagingRequest request, CancellationToken cancellationToken)
    {
        if ((request.BuiltInSource is null) == (request.ExtensionId is null))
            throw new AgwException(ErrorCodes.InvalidParam);

        EventSource source;
        if (request.BuiltInSource is { } builtInName)
        {
            var declaration = EventBuiltInSources.Get(builtInName);
            if (!EventTypePatterns.MatchesAny(declaration.EventTypes, request.Event.Type))
                throw new AgwException(ErrorCodes.InvalidParam);
            source = await _publisherSources.EnsureBuiltInAsync(builtInName, cancellationToken);
        }
        else
        {
            if (EventTypePatterns.IsReserved(request.Event.Type))
                throw new AgwException(ErrorCodes.EventTypeReserved);
            source = await _publisherSources.EnsureExtensionAsync(request.ExtensionId!.Value, cancellationToken);
        }

        var draft = request.Event with { Source = request.Event.Source ?? source.CeSource };
        if (!BelongsToSource(draft.Source!, source.CeSource))
            throw new AgwException(ErrorCodes.InvalidParam, "CloudEvents source is outside the publisher source.");
        return await _records.StageSourceAsync(source, draft, request, cancellationToken);
    }

    private static bool BelongsToSource(string source, string prefix) =>
        source.Equals(prefix, StringComparison.Ordinal) || source.StartsWith(prefix + "/", StringComparison.Ordinal);
}
