using Agw.Jobs.Application.Services;
using Agw.Jobs.Contracts;
using Bens.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using HttpResult = Microsoft.AspNetCore.Http.IResult;

namespace Agw.Jobs.Api;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapJobsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var jobs = endpoints.MapGroup("api/jobs").WithTags("Jobs");
        jobs.MapGet("list", ListAsync).Produces<ApiResult<JobResponse[]>>();
        jobs.MapGet("get", GetAsync).Produces<ApiResult<JobResponse>>();
        jobs.MapPost("create", CreateAsync).Produces<ApiResult<JobResponse>>();
        jobs.MapPost("update", UpdateAsync).Produces<ApiResult<JobResponse>>();
        jobs.MapPost("enable", SetEnabledAsync).Produces<ApiResult<JobResponse>>();
        jobs.MapPost("delete", DeleteAsync).Produces<ApiResult<JobResponse>>();
        jobs.MapGet("runs/list", ListRunsAsync).Produces<ApiResult<JobRunResponse[]>>();
        return endpoints;
    }

    private static async Task<HttpResult> ListAsync(
        Guid? projectId,
        JobAppService service,
        CancellationToken cancellationToken
    ) => ApiResult.Ok(await service.ListAsync(projectId, cancellationToken));

    private static async Task<HttpResult> GetAsync(
        Guid id,
        JobAppService service,
        CancellationToken cancellationToken
    ) => ApiResult.Ok(await service.GetAsync(id, cancellationToken));

    private static async Task<HttpResult> CreateAsync(
        JobCreateRequest request,
        JobAppService service,
        CancellationToken cancellationToken
    ) => ApiResult.Ok(await service.CreateAsync(request, cancellationToken));

    private static async Task<HttpResult> UpdateAsync(
        JobUpdateRequest request,
        JobAppService service,
        CancellationToken cancellationToken
    ) => ApiResult.Ok(await service.UpdateAsync(request, cancellationToken));

    private static async Task<HttpResult> SetEnabledAsync(
        JobEnableRequest request,
        JobAppService service,
        CancellationToken cancellationToken
    ) => ApiResult.Ok(await service.SetEnabledAsync(request, cancellationToken));

    private static async Task<HttpResult> DeleteAsync(
        JobIdRequest request,
        JobAppService service,
        CancellationToken cancellationToken
    ) => ApiResult.Ok(await service.DeleteAsync(request.Id, cancellationToken));

    private static async Task<HttpResult> ListRunsAsync(
        Guid jobId,
        JobAppService service,
        CancellationToken cancellationToken,
        int offset = 0,
        int limit = 100
    ) => ApiResult.Ok(await service.ListRunsAsync(jobId, offset, limit, cancellationToken));
}
