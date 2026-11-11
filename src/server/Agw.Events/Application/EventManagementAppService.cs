using System.Text.Json;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Contracts.Execution;
using Agw.Auth.Contracts;
using Agw.Events.Application.Matching;
using Agw.Events.Application.Persistence;
using Agw.Extensions.Contracts;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Events.Application;

public sealed class EventManagementAppService
{
    private readonly IEventsDbContext _db;
    private readonly IAgentCatalogFacade _agents;
    private readonly IProjectOwnershipFacade _projects;
    private readonly IExtensionGenerationCatalog _extensions;

    public EventManagementAppService(
        IEventsDbContext db,
        IAgentCatalogFacade agents,
        IProjectOwnershipFacade projects,
        IExtensionGenerationCatalog extensions
    )
    {
        _db = db;
        _agents = agents;
        _projects = projects;
        _extensions = extensions;
    }

    public async Task<EventSubscriptionResponse[]> ListSubscriptionsAsync(CancellationToken cancellationToken) =>
        (await _db.EventSubscriptions.AsNoTracking().OrderBy(row => row.Name).ToListAsync(cancellationToken))
            .Select(Subscription)
            .ToArray();

    public async Task<EventSubscriptionResponse> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken) =>
        Subscription(await FindSubscriptionAsync(id, cancellationToken));

    public async Task<EventSubscriptionResponse> SaveSubscriptionAsync(
        EventSubscriptionRequest request,
        bool create,
        CancellationToken cancellationToken
    )
    {
        EventSubscription subscription;
        if (create)
        {
            if (request.Id.HasValue || request.Version.HasValue)
                throw new AgwException(ErrorCodes.InvalidParam);
            subscription = new EventSubscription { Id = Guid.CreateVersion7(), CreateBy = UserInfoUtil.RequiredUserId };
        }
        else
        {
            subscription = await FindSubscriptionAsync(
                request.Id ?? throw new AgwException(ErrorCodes.InvalidParam),
                cancellationToken
            );
            CheckEditable(subscription, request.Version);
        }
        await ValidateAsync(request, cancellationToken);
        subscription.Name = request.Name.Trim();
        subscription.Enabled = request.Enabled;
        subscription.SourceIdsJson = JsonSerializer.Serialize(request.SourceIds.Distinct());
        subscription.PublisherKeysJson = JsonSerializer.Serialize(
            request.PublisherKeys.Distinct(StringComparer.Ordinal)
        );
        subscription.EventTypesJson = JsonSerializer.Serialize(request.EventTypes.Distinct(StringComparer.Ordinal));
        subscription.FilterJson = request.Filter?.GetRawText();
        subscription.TargetKind = request.TargetKind;
        subscription.AgentId = request.AgentId;
        subscription.ProjectId = request.ProjectId;
        subscription.Instructions = request.Instructions;
        subscription.PermissionMode = request.PermissionMode;
        subscription.HandlerExtensionId = request.HandlerExtensionId;
        subscription.HandlerId = request.HandlerId;
        subscription.HandlerConfigJson = request.HandlerConfig?.GetRawText();
        if (create)
            _db.EventSubscriptions.Add(subscription);
        await SaveAsync(cancellationToken);
        return Subscription(subscription);
    }

    public async Task DeleteSubscriptionAsync(
        EventSubscriptionDeleteCommand request,
        CancellationToken cancellationToken
    )
    {
        var subscription = await FindSubscriptionAsync(request.Id, cancellationToken);
        CheckEditable(subscription, request.Version);
        _db.EventSubscriptions.Remove(subscription);
        await SaveAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EventView>> ListEventsAsync(
        EventListFilter query,
        CancellationToken cancellationToken
    )
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 500)
            throw new AgwException(ErrorCodes.InvalidParam);
        var records = _db.Events.AsNoTracking().AsQueryable();
        if (query.SourceId.HasValue)
            records = records.Where(row => row.SourceId == query.SourceId);
        if (query.Type is not null)
            records = records.Where(row => row.Type == query.Type);
        if (query.Subject is not null)
            records = records.Where(row => row.Subject == query.Subject);
        if (query.ChainId.HasValue)
            records = records.Where(row => row.ChainId == query.ChainId);
        if (query.From.HasValue)
            records = records.Where(row => row.ReceivedAt >= query.From);
        if (query.To.HasValue)
            records = records.Where(row => row.ReceivedAt <= query.To);
        return (
            await records
                .OrderByDescending(row => row.ReceivedAt)
                .ThenByDescending(row => row.Id)
                .Skip(query.Offset)
                .Take(query.Limit)
                .ToListAsync(cancellationToken)
        )
            .Select(Event)
            .ToArray();
    }

    public async Task<EventView> GetEventAsync(Guid id, CancellationToken cancellationToken) =>
        Event(
            await _db.Events.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken)
                ?? throw new AgwException(ErrorCodes.ResourceNotFound)
        );

    public async Task<EventDeliveryResponse[]> ListDeliveriesAsync(
        Guid? eventId,
        EventDeliveryState? state,
        CancellationToken cancellationToken
    ) =>
        await _db
            .EventDeliveries.AsNoTracking()
            .Where(row => (!eventId.HasValue || row.EventId == eventId) && (!state.HasValue || row.State == state))
            .OrderByDescending(row => row.Id)
            .Take(200)
            .Select(row => new EventDeliveryResponse
            {
                Id = row.Id,
                EventId = row.EventId,
                SubscriptionId = row.SubscriptionId,
                State = row.State,
                Attempt = row.Attempt,
                LastError = row.LastError,
                NextAttemptAt = row.NextAttemptAt,
                CompletedAt = row.CompletedAt,
            })
            .ToArrayAsync(cancellationToken);

    public async Task<EventScheduleResponse[]> ListSchedulesAsync(CancellationToken cancellationToken) =>
        await _db
            .EventSchedules.AsNoTracking()
            .OrderBy(row => row.DueAt)
            .Take(200)
            .Select(row => new EventScheduleResponse
            {
                Id = row.Id,
                Kind = row.Kind,
                DueAt = row.DueAt,
                Interval = row.Interval,
                Cron = row.Cron,
                TimeZone = row.TimeZone,
                EventType = row.EventType,
                Subject = row.Subject,
                Summary = row.Summary,
                State = row.State,
                Occurrence = row.Occurrence,
                LastFiredAt = row.LastFiredAt,
            })
            .ToArrayAsync(cancellationToken);

    private async Task<EventSubscription> FindSubscriptionAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.EventSubscriptions.SingleOrDefaultAsync(row => row.Id == id, cancellationToken)
        ?? throw new AgwException(ErrorCodes.EventSubscriptionNotFound);

    private async Task ValidateAsync(EventSubscriptionRequest request, CancellationToken cancellationToken)
    {
        if (
            string.IsNullOrWhiteSpace(request.Name)
            || request.EventTypes.Count == 0
            || request.EventTypes.Any(pattern =>
                string.IsNullOrWhiteSpace(pattern)
                || pattern.Contains('*')
                    && (!pattern.EndsWith(".*", StringComparison.Ordinal) || pattern[..^1].Contains('*'))
            )
            || !Enum.IsDefined(request.TargetKind)
        )
            throw new AgwException(ErrorCodes.InvalidParam);
        var sourceIds = request.SourceIds.Distinct().ToArray();
        if (
            await _db.EventSources.CountAsync(
                row => sourceIds.Contains(row.Id) && row.DeletedAt == null,
                cancellationToken
            ) != sourceIds.Length
        )
            throw new AgwException(ErrorCodes.EventSourceNotFound);
        EventSubscriptionFilterValidator.Validate(request.Filter);
        if (request.TargetKind == EventSubscriptionTargetKind.ExtensionHandler)
        {
            if (
                request.HandlerExtensionId is not { } extensionId
                || string.IsNullOrWhiteSpace(request.HandlerId)
                || !_extensions
                    .GetSnapshots()
                    .Any(snapshot =>
                        snapshot.GenerationId.ExtensionId == extensionId
                        && snapshot.State == ExtensionGenerationState.Active
                        && snapshot.EventHandlers.Any(handler => handler.Id == request.HandlerId)
                    )
            )
                throw new AgwException(ErrorCodes.InvalidParam);
        }
        else
        {
            var type =
                request.TargetKind == EventSubscriptionTargetKind.Agent
                    ? AgentRuntimeType.Agent
                    : AgentRuntimeType.Agentflow;
            if (
                request.AgentId is not { } agentId
                || request.ProjectId is not { } projectId
                || !await _agents.IsOwnedTargetAsync(type, agentId, UserInfoUtil.RequiredUserId, cancellationToken)
                || !await _projects.IsOwnedAsync(projectId, cancellationToken)
                || request.PermissionMode is not { } permissionMode
                || !Enum.IsDefined((AgentExecutionPermissionMode)permissionMode)
            )
                throw new AgwException(ErrorCodes.InvalidParam);
        }
    }

    private static void CheckEditable(EventSubscription subscription, long? version)
    {
        if (subscription.ManagedByExtensionId.HasValue)
            throw new AgwException(ErrorCodes.EventSubscriptionManaged);
        if (version != subscription.Version)
            throw new AgwException(ErrorCodes.EventSubscriptionModificationConflict);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new AgwException(
                ErrorCodes.EventSubscriptionModificationConflict,
                ErrorCodes.EventSubscriptionModificationConflict.Message,
                exception
            );
        }
    }

    private static EventSubscriptionResponse Subscription(EventSubscription row) =>
        new()
        {
            Id = row.Id,
            Name = row.Name,
            Enabled = row.Enabled,
            Version = row.Version,
            SourceIds = JsonSerializer.Deserialize<Guid[]>(row.SourceIdsJson)!,
            PublisherKeys = JsonSerializer.Deserialize<string[]>(row.PublisherKeysJson ?? "[]")!,
            EventTypes = JsonSerializer.Deserialize<string[]>(row.EventTypesJson)!,
            Filter = Parse(row.FilterJson),
            TargetKind = row.TargetKind,
            AgentId = row.AgentId,
            ProjectId = row.ProjectId,
            Instructions = row.Instructions,
            PermissionMode = row.PermissionMode,
            HandlerExtensionId = row.HandlerExtensionId,
            HandlerId = row.HandlerId,
            HandlerConfig = Parse(row.HandlerConfigJson),
            ManagedByExtensionId = row.ManagedByExtensionId,
            ManagedKey = row.ManagedKey,
            ManagedVersion = row.ManagedVersion,
            ManagedState = row.ManagedState,
        };

    private static EventView Event(EventRecord row) =>
        new()
        {
            Id = row.Id,
            SourceId = row.SourceId,
            ReceivedAt = row.ReceivedAt,
            ChainId = row.ChainId,
            Hops = row.Hops,
            DispatchState = row.DispatchState,
            MatchedCount = row.MatchedCount,
            CloudEvent = new EventCloudEvent
            {
                Id = row.CeId,
                Source = row.CeSource,
                Type = row.Type,
                Time = row.CeTime,
                Subject = row.Subject,
                DataContentType = row.CeDataContentType,
                DataSchema = row.CeDataSchema,
                Data = ReadData(row),
                Scope = row.Scope,
                Tags = row.TagsJson is null ? null : JsonSerializer.Deserialize<string[]>(row.TagsJson),
                Summary = row.Summary,
                Link = row.Link,
                ParentEventId = row.ParentEventId,
            },
        };

    private static CloudEventData? ReadData(EventRecord row) =>
        CloudEventDataCodec.ReadData(row.CeData, row.CeDataContentType, row.CeDataIsBinary);

    private static JsonElement? Parse(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);
}
