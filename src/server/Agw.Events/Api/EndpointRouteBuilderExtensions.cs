using Agw.Events.Api.Dtos;
using Agw.Events.Application;
using Agw.Events.Application.Delivery;
using Agw.Events.Application.Persistence;
using Agw.Shared.Data.Entities.Events;
using Bens.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agw.Events.Api;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapEventsApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("api/events").WithTags("Events").RequireAuthorization();
        group
            .MapGet(
                "subscriptions/list",
                async (EventManagementAppService service, CancellationToken ct) =>
                    ApiResult.Ok(await service.ListSubscriptionsAsync(ct))
            )
            .Produces<ApiResult<EventSubscriptionResponse[]>>();
        group
            .MapGet(
                "subscriptions/get",
                async (Guid id, EventManagementAppService service, CancellationToken ct) =>
                    ApiResult.Ok(await service.GetSubscriptionAsync(id, ct))
            )
            .Produces<ApiResult<EventSubscriptionResponse>>();
        group
            .MapPost(
                "subscriptions/create",
                async (EventSubscriptionRequest request, EventManagementAppService service, CancellationToken ct) =>
                    ApiResult.Ok(await service.SaveSubscriptionAsync(request, true, ct))
            )
            .Produces<ApiResult<EventSubscriptionResponse>>();
        group
            .MapPost(
                "subscriptions/update",
                async (EventSubscriptionRequest request, EventManagementAppService service, CancellationToken ct) =>
                    ApiResult.Ok(await service.SaveSubscriptionAsync(request, false, ct))
            )
            .Produces<ApiResult<EventSubscriptionResponse>>();
        group
            .MapPost(
                "subscriptions/delete",
                async (
                    EventSubscriptionDeleteRequest request,
                    EventManagementAppService service,
                    CancellationToken ct
                ) =>
                {
                    await service.DeleteSubscriptionAsync(
                        new EventSubscriptionDeleteCommand(request.Id, request.Version),
                        ct
                    );
                    return ApiResult.Ok();
                }
            )
            .Produces<ApiResult>();
        group
            .MapGet(
                "list",
                async ([AsParameters] EventListQuery query, EventManagementAppService service, CancellationToken ct) =>
                    ApiResult.Ok((await service.ListEventsAsync(ToFilter(query), ct)).Select(ToResponse).ToArray())
            )
            .Produces<ApiResult<EventResponse[]>>();
        group
            .MapGet(
                "get",
                async (Guid id, EventManagementAppService service, CancellationToken ct) =>
                    ApiResult.Ok(ToResponse(await service.GetEventAsync(id, ct)))
            )
            .Produces<ApiResult<EventResponse>>();
        group
            .MapGet(
                "chain",
                async (Guid chainId, EventManagementAppService service, CancellationToken ct) =>
                    ApiResult.Ok(
                        (await service.ListEventsAsync(new EventListFilter { ChainId = chainId, Limit = 500 }, ct))
                            .Select(ToResponse)
                            .ToArray()
                    )
            )
            .Produces<ApiResult<EventResponse[]>>();
        group
            .MapGet(
                "deliveries/list",
                async (
                    Guid? eventId,
                    EventDeliveryState? state,
                    EventManagementAppService service,
                    CancellationToken ct
                ) => ApiResult.Ok(await service.ListDeliveriesAsync(eventId, state, ct))
            )
            .Produces<ApiResult<EventDeliveryResponse[]>>();
        group
            .MapGet(
                "schedules/list",
                async (EventManagementAppService service, CancellationToken ct) =>
                    ApiResult.Ok(await service.ListSchedulesAsync(ct))
            )
            .Produces<ApiResult<EventScheduleResponse[]>>();
        group
            .MapPost(
                "deliveries/retry",
                async (EventIdRequest request, EventDeliveryManagementAppService service, CancellationToken ct) =>
                    ApiResult.Ok(new EventDeliveryActionResponse(await service.RetryAsync(request.Id, ct)))
            )
            .Produces<ApiResult<EventDeliveryActionResponse>>();
        group
            .MapPost(
                "deliveries/cancel",
                async (EventIdRequest request, EventDeliveryManagementAppService service, CancellationToken ct) =>
                    ApiResult.Ok(new EventDeliveryActionResponse(await service.CancelAsync(request.Id, ct)))
            )
            .Produces<ApiResult<EventDeliveryActionResponse>>();
        group
            .MapGet(
                "unpublished/list",
                async (IEventRecoveryControl recovery, CancellationToken ct) =>
                    ApiResult.Ok(await recovery.ListAsync(ct))
            )
            .Produces<ApiResult<UnpublishedEventRecord[]>>();
        group
            .MapPost(
                "unpublished/republish",
                async (EventRepublishRequest request, IEventRecoveryControl recovery, CancellationToken ct) =>
                {
                    await recovery.RepublishAsync(request.Topic, request.EntityId, ct);
                    return ApiResult.Ok();
                }
            )
            .Produces<ApiResult>();
        return endpoints;
    }

    private static EventListFilter ToFilter(EventListQuery query) =>
        new()
        {
            SourceId = query.SourceId,
            Type = query.Type,
            Subject = query.Subject,
            ChainId = query.ChainId,
            From = query.From,
            To = query.To,
            Offset = query.Offset,
            Limit = query.Limit,
        };

    private static EventResponse ToResponse(EventView view) =>
        new()
        {
            Id = view.Id,
            SourceId = view.SourceId,
            CloudEvent = CloudEventResponseMapper.From(view.CloudEvent),
            Summary = view.CloudEvent.Summary,
            Link = view.CloudEvent.Link,
            ParentEventId = view.CloudEvent.ParentEventId,
            ReceivedAt = view.ReceivedAt,
            ChainId = view.ChainId,
            Hops = view.Hops,
            DispatchState = view.DispatchState,
            MatchedCount = view.MatchedCount,
        };
}

public sealed record EventRepublishRequest(string Topic, Guid EntityId);

public sealed record EventDeliveryActionResponse(bool Accepted);
