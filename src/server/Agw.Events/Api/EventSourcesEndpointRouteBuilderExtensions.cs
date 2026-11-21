using Agw.Events.Api.Dtos;
using Agw.Events.Application.Persistence;
using Agw.Events.Application.Sources;
using Agw.Events.Configuration;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using HttpResult = Microsoft.AspNetCore.Http.IResult;

namespace Agw.Events.Api;

public static class EventSourcesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapEventSourcesApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var sources = endpoints.MapGroup("api/events/sources").WithTags("Event Sources").RequireAuthorization();

        sources.MapGet("list", ListAsync).Produces<ApiResult<PagedResult<EventSourceResponse>>>();
        sources.MapGet("options", ListOptionsAsync).Produces<ApiResult<EventSourceOptionResponse[]>>();
        sources.MapGet("providers", ListProviders).Produces<ApiResult<EventSourceProviderResponse[]>>();
        sources.MapGet("http-poll-options", GetHttpPollOptions).Produces<ApiResult<EventHttpPollFormOptionsResponse>>();
        sources.MapGet("get", GetAsync).Produces<ApiResult<EventSourceResponse>>();
        sources.MapPost("create", CreateAsync).Produces<ApiResult<EventSourceMutationResponse>>();
        sources.MapPost("update", UpdateAsync).Produces<ApiResult<EventSourceMutationResponse>>();
        sources.MapPost("delete", DeleteAsync).Produces<ApiResult>();
        sources.MapPost("enable", SetEnabledAsync).Produces<ApiResult<EventSourceMutationResponse>>();
        sources.MapPost("rotate-secret", RotateSecretAsync).Produces<ApiResult<EventSourceMutationResponse>>();
        sources.MapPost("test", TestAsync).Produces<ApiResult<EventSourceTestResponse>>();

        endpoints
            .MapPost(EventWebhookRoute.Receive, ReceiveWebhookAsync)
            .WithTags("Events")
            .AllowAnonymous()
            .Produces<ApiResult<EventWebhookReceiveResponse>>(StatusCodes.Status202Accepted);

        return endpoints;
    }

    private static async Task<HttpResult> ListAsync(
        EventSourceAppService service,
        CancellationToken cancellationToken,
        int pageIndex = 1,
        int pageSize = 20
    )
    {
        var page = await service.ListAsync(pageIndex, pageSize, cancellationToken);
        return ApiResult.Ok(
            new PagedResult<EventSourceResponse>
            {
                Items = page.Items.Select(source => ToResponse(source, service.GetEventTypes(source))).ToArray(),
                Total = page.Total,
                PageIndex = page.PageIndex,
                PageSize = page.PageSize,
            }
        );
    }

    private static async Task<HttpResult> ListOptionsAsync(
        EventSourceAppService service,
        CancellationToken cancellationToken
    )
    {
        var options = await service.ListOptionsAsync(cancellationToken);
        return ApiResult.Ok(
            options
                .Select(option => new EventSourceOptionResponse
                {
                    Id = option.Id,
                    Name = option.Name,
                    Kind = option.Kind,
                    ManagedBy = option.ManagedBy,
                    EventTypes = option.EventTypes,
                })
                .ToArray()
        );
    }

    private static HttpResult ListProviders(EventSourceAppService service) =>
        ApiResult.Ok(
            service
                .ListProviders()
                .Select(provider => new EventSourceProviderResponse
                {
                    ExtensionId = provider.ExtensionId,
                    ExtensionName = provider.ExtensionName,
                    ExtensionDisplayName = provider.ExtensionDisplayName,
                    ProviderId = provider.ProviderId,
                    EventTypes = provider.EventTypes,
                    Configurations = provider
                        .Configurations.Select(configuration => new EventSourceProviderConfigurationResponse
                        {
                            Id = configuration.Id,
                            Name = configuration.Name,
                            Description = configuration.Description,
                        })
                        .ToArray(),
                })
                .ToArray()
        );

    private static HttpResult GetHttpPollOptions(IOptions<EventsOptions> options, string? cursorPath = null) =>
        ApiResult.Ok(
            new EventHttpPollFormOptionsResponse
            {
                MaxPages = options.Value.HttpPoll.MaxPages,
                CursorPathValid = cursorPath is null ? null : EventSourceConfigParser.IsValidJsonPath(cursorPath),
            }
        );

    private static async Task<HttpResult> GetAsync(
        Guid id,
        EventSourceAppService service,
        CancellationToken cancellationToken
    )
    {
        var source = await service.GetAsync(id, cancellationToken);
        return source is null
            ? ErrorCodes.EventSourceNotFound.ToApiResult()
            : ApiResult.Ok(ToResponse(source, service.GetEventTypes(source)));
    }

    private static async Task<HttpResult> CreateAsync(
        EventSourceCreateRequest request,
        EventSourceAppService service,
        CancellationToken cancellationToken
    )
    {
        var result = await service.CreateAsync(
            new EventSourceCreateCommand
            {
                Name = request.Name,
                Kind = request.Kind,
                Config = request.Config,
                Enabled = request.Enabled,
                ProviderExtensionId = request.ProviderExtensionId,
                ProviderId = request.ProviderId,
                Secret = request.Secret,
            },
            cancellationToken
        );
        return ApiResult.Ok(ToResponse(result));
    }

    private static async Task<HttpResult> UpdateAsync(
        EventSourceUpdateRequest request,
        EventSourceAppService service,
        CancellationToken cancellationToken
    )
    {
        var result = await service.UpdateAsync(
            new EventSourceUpdateCommand
            {
                Id = request.Id,
                Version = request.Version,
                Name = request.Name,
                Config = request.Config,
                Secret = request.Secret,
            },
            cancellationToken
        );
        return ApiResult.Ok(ToResponse(result));
    }

    private static async Task<HttpResult> DeleteAsync(
        EventSourceVersionRequest request,
        EventSourceAppService service,
        CancellationToken cancellationToken
    )
    {
        await service.DeleteAsync(
            new EventSourceVersionCommand { Id = request.Id, Version = request.Version },
            cancellationToken
        );
        return ApiResult.Ok();
    }

    private static async Task<HttpResult> SetEnabledAsync(
        EventSourceEnableRequest request,
        EventSourceAppService service,
        CancellationToken cancellationToken
    )
    {
        var result = await service.SetEnabledAsync(
            new EventSourceEnableCommand
            {
                Id = request.Id,
                Version = request.Version,
                Enabled = request.Enabled,
            },
            cancellationToken
        );
        return ApiResult.Ok(ToResponse(result));
    }

    private static async Task<HttpResult> RotateSecretAsync(
        EventSourceVersionRequest request,
        EventSourceAppService service,
        CancellationToken cancellationToken
    )
    {
        var result = await service.RotateSecretAsync(
            new EventSourceVersionCommand { Id = request.Id, Version = request.Version },
            cancellationToken
        );
        return ApiResult.Ok(ToResponse(result));
    }

    private static async Task<HttpResult> TestAsync(
        EventSourceIdRequest request,
        EventSourceAppService service,
        CancellationToken cancellationToken
    )
    {
        var result = await service.TestAsync(request.Id, cancellationToken);
        return ApiResult.Ok(new EventSourceTestResponse { ItemCount = result.ItemCount });
    }

    private static async Task<HttpResult> ReceiveWebhookAsync(
        Guid sourceId,
        HttpContext context,
        IEventWebhookReceiver receiver,
        CancellationToken cancellationToken
    )
    {
        var result = await receiver.ReceiveAsync(sourceId, context, cancellationToken);
        return Results.Json(
            ApiResult.Ok(new EventWebhookReceiveResponse { EventId = result.EventId, Discarded = result.Discarded }),
            statusCode: StatusCodes.Status202Accepted
        );
    }

    private static EventSourceResponse ToResponse(EventSource source, IReadOnlyList<string> eventTypes)
    {
        using var config = System.Text.Json.JsonDocument.Parse(source.ConfigJson);
        return new EventSourceResponse
        {
            Id = source.Id,
            Name = source.Name,
            Kind = source.Kind,
            ProviderExtensionId = source.ProviderExtensionId,
            ProviderId = source.ProviderId,
            ManagedBy = source.ManagedBy,
            ManagedKey = source.ManagedKey,
            CeSource = source.CeSource,
            Config = config.RootElement.Clone(),
            EventTypes = eventTypes,
            Enabled = source.Enabled,
            BaselineAt = source.BaselineAt,
            NextRunAt = source.NextRunAt,
            LastRunAt = source.LastRunAt,
            LastError = source.LastError,
            LastDiscardedType = source.LastDiscardedType,
            LastDiscardedAt = source.LastDiscardedAt,
            HasSecret = source.SecretJson is not null,
            Version = source.Version,
        };
    }

    private static EventSourceMutationResponse ToResponse(EventSourceMutationResult result) =>
        new()
        {
            Id = result.Id,
            Version = result.Version,
            Secret = result.Secret,
        };
}
