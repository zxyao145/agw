using System.Text.Json;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Events.Sources;

public sealed class EventSourceScanWorker
{
    private readonly IEventsDbContext _db;
    private readonly EventHttpPoller _httpPoller;
    private readonly ExtensionEventSourcePoller _extensionPoller;
    private readonly EventStaging _staging;
    private readonly IEventIngestTransaction _transaction;
    private readonly IApplicationLock _applicationLock;
    private readonly PendingTransportMessages _pending;
    private readonly EventsOptions _options;
    private readonly TimeProvider _timeProvider;

    public EventSourceScanWorker(
        IEventsDbContext db,
        EventHttpPoller httpPoller,
        ExtensionEventSourcePoller extensionPoller,
        EventStaging staging,
        IEventIngestTransaction transaction,
        IApplicationLock applicationLock,
        PendingTransportMessages pending,
        IOptions<EventsOptions> options,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _httpPoller = httpPoller;
        _extensionPoller = extensionPoller;
        _staging = staging;
        _transaction = transaction;
        _applicationLock = applicationLock;
        _pending = pending;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task ScanAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        var source =
            await _db.EventSources.SingleOrDefaultAsync(
                row => row.Id == sourceId && row.Enabled && row.DeletedAt == null,
                cancellationToken
            ) ?? throw new AgwException(ErrorCodes.EventSourceNotFound);
        if (source.Kind is not (EventSourceKind.HttpPoll or EventSourceKind.Provider))
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        await _pending.AcquireAsync(_applicationLock, $"event-source:{source.Id}", cancellationToken);
        var now = _timeProvider.GetUtcNow();
        if (source.Kind == EventSourceKind.HttpPoll)
        {
            await ScanHttpPollAsync(source, now, cancellationToken);
            return;
        }

        await ScanProviderAsync(source, now, cancellationToken);
    }

    public async Task RecordFailureAsync(Guid sourceId, string error, CancellationToken cancellationToken)
    {
        var source = await _db.EventSources.SingleOrDefaultAsync(
            row => row.Id == sourceId && row.Enabled && row.DeletedAt == null,
            cancellationToken
        );
        if (source is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        source.LastRunAt = now;
        source.LastError = error.Length > 2000 ? error[..2000] : error;
        source.NextRunAt = now + _options.HttpPoll.MinInterval;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task ScanHttpPollAsync(EventSource source, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var configDocument = JsonDocument.Parse(source.ConfigJson);
        var config = EventSourceConfigParser.ParseHttpPoll(configDocument.RootElement, _options.HttpPoll);
        var observed = await _httpPoller.FetchAsync(source, config, cancellationToken);
        var savedItems = await _db
            .EventSourceItems.Where(item => item.SourceId == source.Id)
            .ToArrayAsync(cancellationToken);
        var baseline = source.BaselineAt is null;
        var diff = HttpPollDiff.Diff(source.Id, savedItems, observed, baseline, now);
        var savedIds = savedItems.Select(item => item.Id).ToHashSet();
        var newItems = diff.Items.Where(item => !savedIds.Contains(item.Id)).ToArray();
        var interval = System.Xml.XmlConvert.ToTimeSpan(config.Interval);

        await _transaction.ExecuteAsync(
            async token =>
            {
                _db.EventSourceItems.AddRange(newItems);
                if (!baseline)
                {
                    var itemsByKey = diff.Items.ToDictionary(item => item.ItemKey, StringComparer.Ordinal);
                    foreach (var change in diff.Changes)
                    {
                        var draft = EventHttpPoller.CreateEventDraft(source, config, change);
                        var result = await _staging.StageSourceAsync(source, draft, null, token);
                        itemsByKey[change.ItemKey].LastEventId = result.EventId;
                    }
                }

                source.BaselineAt ??= now;
                source.LastRunAt = now;
                source.NextRunAt = now + interval;
                source.LastError = null;
            },
            cancellationToken
        );
    }

    private async Task ScanProviderAsync(EventSource source, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var baseline = source.BaselineAt is null;
        var result = await _extensionPoller.PollAsync(source, baseline, cancellationToken);
        await _transaction.ExecuteAsync(
            async token =>
            {
                if (!baseline)
                {
                    foreach (var draft in result.Events)
                    {
                        if (draft.Type.StartsWith("agw.", StringComparison.Ordinal))
                        {
                            throw new AgwException(ErrorCodes.EventTypeReserved);
                        }

                        _ = await _staging.StageSourceAsync(
                            source,
                            draft with
                            {
                                Source = draft.Source ?? source.CeSource,
                            },
                            null,
                            token
                        );
                    }
                }

                source.CursorJson = result.Cursor?.GetRawText();
                source.BaselineAt ??= now;
                source.LastRunAt = now;
                source.NextRunAt = now + _options.HttpPoll.MinInterval;
                source.LastError = null;
            },
            cancellationToken
        );
    }
}
