using System.Security.Claims;

using Agw.Jobs.Application.Contracts;
using Agw.Jobs.Application.Services;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;

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
        var routeGroup = endpoints.MapGroup("api")
            .WithTags("Jobs");

        routeGroup.MapGet("jobs", ListAsync)
            .Produces<ApiResult<Job[]>>();
        routeGroup.MapGet("jobs/{id:guid}", GetAsync)
            .Produces<ApiResult<Job>>();
        routeGroup.MapGet("jobs/{id:guid}/logs", ListLogsAsync)
            .Produces<ApiResult<JobLogResponse[]>>();
        routeGroup.MapPost("jobs", CreateAsync)
            .Produces<ApiResult<Job>>();
        routeGroup.MapPut("jobs/{id:guid}", UpdateAsync)
            .Produces<ApiResult<Job>>();
        routeGroup.MapDelete("jobs/{id:guid}", DeleteAsync)
            .Produces<ApiResult>();

        return endpoints;
    }

    private static async Task<HttpResult> ListAsync(
        JobAppService jobAppService,
        CancellationToken cancellationToken)
    {
        var jobs = await jobAppService.ListAsync(cancellationToken);
        return ApiResult.Ok(jobs);
    }

    private static async Task<HttpResult> GetAsync(Guid id, JobAppService jobAppService)
    {
        var job = await jobAppService.GetAsync(id);
        return job == null
            ? ErrorCodes.ResourceNotFound.ToApiResult()
            : ApiResult.Ok(job);
    }

    private static async Task<HttpResult> ListLogsAsync(
        Guid id,
        JobAppService jobAppService,
        CancellationToken cancellationToken)
    {
        var logs = await jobAppService.ListLogsAsync(id, cancellationToken);
        return ApiResult.Ok(logs);
    }

    private static async Task<HttpResult> CreateAsync(
        JobCreateRequest request,
        JobAppService jobAppService,
        ClaimsPrincipal user)
    {
        var userName = user.Identity?.Name ?? "system";
        var job = await jobAppService.CreateAsync(request, userName);
        return ApiResult.Ok(job);
    }

    private static async Task<HttpResult> UpdateAsync(
        Guid id,
        JobUpdateRequest request,
        JobAppService jobAppService,
        ClaimsPrincipal user)
    {
        var userName = user.Identity?.Name ?? "system";
        var job = await jobAppService.UpdateAsync(id, request, userName);
        return job == null
            ? ErrorCodes.ResourceNotFound.ToApiResult()
            : ApiResult.Ok(job);
    }

    private static async Task<HttpResult> DeleteAsync(Guid id, JobAppService jobAppService)
    {
        var deleted = await jobAppService.DeleteAsync(id);
        return deleted
            ? ApiResult.Ok()
            : ErrorCodes.ResourceNotFound.ToApiResult();
    }
}
