using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Notifications.Api.Dtos;
using Agw.Notifications.Application;
using Agw.Notifications.Contracts;
using Agw.Notifications.Infrastructure.Stream;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using HttpResult = Microsoft.AspNetCore.Http.IResult;

namespace Agw.Notifications.Api;

public static class EndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapNotificationsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var routeGroup = endpoints.MapGroup("api/notifications").WithTags("Notifications").RequireAuthorization();

        routeGroup.MapGet("list", ListAsync).Produces<ApiResult<NotificationListItem[]>>();
        routeGroup.MapGet("get", GetAsync).Produces<ApiResult<NotificationDetail>>();
        routeGroup.MapGet("stream", StreamAsync).Produces(StatusCodes.Status200OK, contentType: "text/event-stream");
        routeGroup.MapPost("read", ReadAsync).Produces<ApiResult>();
        routeGroup.MapPost("answer", AnswerAsync).Produces<ApiResult<NotificationAnswerResult>>();
        routeGroup.MapPost("delivered", DeliveredAsync).Produces<ApiResult>();
        routeGroup.MapGet("channels", ChannelsAsync).Produces<ApiResult<NotificationChannelResponse[]>>();
        routeGroup.MapPost("dispatches/retry", RetryDispatchAsync).Produces<ApiResult>();

        return endpoints;
    }

    private static async Task<HttpResult> ListAsync(
        bool unreadOnly,
        NotificationAppService notificationAppService,
        CancellationToken cancellationToken
    ) => ApiResult.Ok(await notificationAppService.ListAsync(unreadOnly, cancellationToken).ConfigureAwait(false));

    private static async Task<HttpResult> GetAsync(
        Guid id,
        NotificationAppService notificationAppService,
        CancellationToken cancellationToken
    )
    {
        var notification = await notificationAppService.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return notification is null ? ErrorCodes.NotificationNotFound.ToApiResult() : ApiResult.Ok(notification);
    }

    private static async Task StreamAsync(
        HttpContext context,
        NotificationStreamHub streamHub,
        TimeProvider timeProvider
    )
    {
        var userId = UserInfoUtil.RequiredUserId;
        var lastEventId = context.Request.Headers["Last-Event-ID"].ToString();
        var connectedAt = timeProvider.GetUtcNow();
        await using var subscription = streamHub.Subscribe(userId, lastEventId, connectedAt);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
        var ready = JsonSerializer.Serialize(new { connectedAt }, JsonOptions);
        await context
            .Response.WriteAsync($"event: ready\ndata: {ready}\n\n", context.RequestAborted)
            .ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        await foreach (var delivery in subscription.Reader.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
        {
            var json = JsonSerializer.Serialize(delivery.Notification, JsonOptions);
            await context
                .Response.WriteAsync(
                    $"id: {delivery.EventId}\nevent: notification\ndata: {json}\n\n",
                    context.RequestAborted
                )
                .ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async Task<HttpResult> ReadAsync(
        NotificationReadRequest request,
        NotificationAppService notificationAppService,
        CancellationToken cancellationToken
    )
    {
        await notificationAppService.MarkReadAsync(request.NotificationId, cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok();
    }

    private static async Task<HttpResult> AnswerAsync(
        NotificationAnswerRequest request,
        NotificationAppService notificationAppService,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return ApiResult.Ok(
                await notificationAppService
                    .SubmitAnswerAsync(
                        new NotificationAnswer
                        {
                            NotificationId = request.NotificationId,
                            Option = request.Option,
                            Text = request.Text,
                        },
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            );
        }
        catch (AgwException exception) when (exception.Code == ErrorCodes.NotificationQuestionAlreadyAnswered.Code)
        {
            return ApiResult
                .New<NotificationAnswerResult>()
                .WithCode(exception.Code)
                .WithTitle(exception.Message)
                .WithStatusCode((int)exception.StatusCode)
                .WithData((NotificationAnswerResult)exception.Data[nameof(NotificationAnswerResult)]!);
        }
    }

    private static async Task<HttpResult> DeliveredAsync(
        NotificationDeliveredRequest request,
        NotificationAppService notificationAppService,
        CancellationToken cancellationToken
    )
    {
        await notificationAppService
            .MarkDesktopDeliveredAsync(request.NotificationId, request.Version, cancellationToken)
            .ConfigureAwait(false);
        return ApiResult.Ok();
    }

    private static async Task<HttpResult> ChannelsAsync(
        NotificationAppService notificationAppService,
        CancellationToken cancellationToken
    ) => ApiResult.Ok(await notificationAppService.ListChannelsAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<HttpResult> RetryDispatchAsync(
        NotificationDispatchRetryRequest request,
        NotificationAppService notificationAppService,
        CancellationToken cancellationToken
    )
    {
        await notificationAppService.RetryDispatchAsync(request.DispatchId, cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok();
    }
}
