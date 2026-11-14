using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application.Matching;
using Agw.Events.Application.Persistence;
using Agw.Events.Contracts;
using Agw.Events.Domain.Behaviors;
using Agw.Events.Domain.ValueObjects;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Events.Application;

public sealed class EventSubscriptionFacade : IEventSubscriptionFacade
{
    private readonly IEventsDbContext _db;
    private readonly EventCaller _caller;
    private readonly IApplicationLock _applicationLock;

    public EventSubscriptionFacade(IEventsDbContext db, EventCaller caller, IApplicationLock applicationLock)
    {
        _db = db;
        _caller = caller;
        _applicationLock = applicationLock;
    }

    public async Task<ManagedSubscriptionState> UpsertManagedAsync(
        ManagedSubscriptionRequest request,
        CancellationToken cancellationToken
    )
    {
        var extension = _caller.RequiredExtension();
        var extensionId = extension.GenerationId.ExtensionId;
        ValidateKey(request.ManagedKey, request.ManagedVersion);
        await using var lease = await _applicationLock.AcquireAsync(
            $"events:subscription:{UserInfoUtil.RequiredUserId}:{extensionId}:{request.ManagedKey}",
            cancellationToken
        );
        var record = await FindAsync(extensionId, request.ManagedKey, cancellationToken);
        if (record?.ManagedVersion >= request.ManagedVersion)
            return State(record, record.ManagedVersion == request.ManagedVersion);
        EventSubscriptionFilterValidator.Validate(request.Filter);
        if (!extension.EventHandlers.Any(handler => handler.Id == request.HandlerId))
            throw new AgwException(ErrorCodes.InvalidParam, "Event handler was not registered by the extension.");
        ValidatePublishers(request.PublisherKeys, extensionId);
        if (request.EventTypes.Count == 0 || request.EventTypes.Any(string.IsNullOrWhiteSpace))
            throw new AgwException(ErrorCodes.InvalidParam);
        var sourceIds = request.SourceIds.Distinct().ToArray();
        if (
            await _db.EventSources.CountAsync(source => sourceIds.Contains(source.Id), cancellationToken)
            != sourceIds.Length
        )
            throw new AgwException(ErrorCodes.EventSourceNotFound);
        record ??= Create(extensionId, request.ManagedKey);
        var accepted = new EventSubscriptionBehavior(record).UpsertManaged(
            request.ManagedVersion,
            new ManagedEventSubscriptionDefinition
            {
                Name = request.Name,
                SourceIdsJson = JsonSerializer.Serialize(sourceIds),
                PublisherKeysJson = JsonSerializer.Serialize(request.PublisherKeys),
                EventTypesJson = JsonSerializer.Serialize(request.EventTypes),
                FilterJson = request.Filter?.GetRawText(),
                HandlerExtensionId = extensionId,
                HandlerId = request.HandlerId,
                HandlerConfigJson = request.HandlerConfig.GetRawText(),
            }
        );
        lease.HandleLostToken.ThrowIfCancellationRequested();
        await _db.SaveChangesAsync(cancellationToken);
        return State(record, accepted);
    }

    public async Task<ManagedSubscriptionState> DeleteManagedAsync(
        string managedKey,
        long managedVersion,
        CancellationToken cancellationToken
    )
    {
        var extensionId = _caller.RequiredExtension().GenerationId.ExtensionId;
        ValidateKey(managedKey, managedVersion);
        await using var lease = await _applicationLock.AcquireAsync(
            $"events:subscription:{UserInfoUtil.RequiredUserId}:{extensionId}:{managedKey}",
            cancellationToken
        );
        var record = await FindAsync(extensionId, managedKey, cancellationToken) ?? Create(extensionId, managedKey);
        var accepted = new EventSubscriptionBehavior(record).DeleteManaged(managedVersion);
        lease.HandleLostToken.ThrowIfCancellationRequested();
        await _db.SaveChangesAsync(cancellationToken);
        return State(record, accepted);
    }

    private Task<EventSubscription?> FindAsync(Guid extensionId, string key, CancellationToken cancellationToken)
    {
        var managedBy = EventManagedBy.ForExtension(extensionId);
        return _db.EventSubscriptions.SingleOrDefaultAsync(
            row => row.ManagedBy == managedBy && row.ManagedKey == key,
            cancellationToken
        );
    }

    private EventSubscription Create(Guid extensionId, string key)
    {
        var record = new EventSubscription
        {
            Id = Guid.CreateVersion7(),
            CreateBy = UserInfoUtil.RequiredUserId,
            ManagedBy = EventManagedBy.ForExtension(extensionId),
            ManagedKey = key,
            TargetKind = EventSubscriptionTargetKind.ExtensionHandler,
        };
        _db.EventSubscriptions.Add(record);
        return record;
    }

    private static ManagedSubscriptionState State(EventSubscription record, bool accepted) =>
        new()
        {
            SubscriptionId = record.Id,
            ManagedVersion = record.ManagedVersion!.Value,
            Status =
                record.ManagedState == EventSubscriptionManagedState.Deleted
                    ? ManagedSubscriptionStatus.Deleted
                    : ManagedSubscriptionStatus.Active,
            Accepted = accepted,
        };

    private static void ValidateKey(string key, long version)
    {
        if (string.IsNullOrWhiteSpace(key) || version < 1)
            throw new AgwException(ErrorCodes.InvalidParam);
    }

    private static void ValidatePublishers(IReadOnlyList<string> publishers, Guid extensionId)
    {
        var allowed = new[]
        {
            "agw.execution",
            "agw.notifications",
            $"ext:{extensionId}",
            $"agw.scheduler:ext:{extensionId}",
        };
        if (publishers.Any(publisher => !allowed.Contains(publisher, StringComparer.Ordinal)))
            throw new AgwException(ErrorCodes.InvalidParam, "Publisher is outside the extension subscription scope.");
    }
}
