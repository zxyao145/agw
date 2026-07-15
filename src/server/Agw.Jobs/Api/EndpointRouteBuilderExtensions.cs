using System.Security.Claims;

using Agw.Jobs.Application.Contracts;
using Agw.Jobs.Application.Services;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agw.Jobs.Api;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapJobsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var routeGroup = endpoints.MapGroup("api")
            .WithTags("jobs");

        routeGroup.MapGet("jobs", ListAsync)
            .Produces<Bens.Results.ApiResult<Job[]>>();
        routeGroup.MapGet("jobs/{id:guid}", GetAsync)
            .Produces<Bens.Results.ApiResult<Job>>();
        routeGroup.MapGet("jobs/{id:guid}/logs", ListLogsAsync)
            .Produces<Bens.Results.ApiResult<JobLogResponse[]>>();
        routeGroup.MapPost("jobs", CreateAsync)
            .Produces<Bens.Results.ApiResult<Job>>();
        routeGroup.MapPut("jobs/{id:guid}", UpdateAsync)
            .Produces<Bens.Results.ApiResult<Job>>();
        routeGroup.MapDelete("jobs/{id:guid}", DeleteAsync)
            .Produces<Bens.Results.ApiResult>();

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        JobAppService jobAppService,
        CancellationToken cancellationToken)
    {
        var jobs = await jobAppService.ListAsync(cancellationToken);
        return AsHttpResult(AgwApiResult.Ok(jobs));
    }

    private static async Task<IResult> GetAsync(Guid id, JobAppService jobAppService)
    {
        var job = await jobAppService.GetAsync(id);
        return AsHttpResult(job == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(job));
    }

    private static async Task<IResult> ListLogsAsync(
        Guid id,
        JobAppService jobAppService,
        CancellationToken cancellationToken)
    {
        var logs = await jobAppService.ListLogsAsync(id, cancellationToken);
        return AsHttpResult(AgwApiResult.Ok(logs));
    }

    private static async Task<IResult> CreateAsync(
        JobCreateRequest request,
        JobAppService jobAppService,
        ClaimsPrincipal user)
    {
        var userName = user.Identity?.Name ?? "system";
        var job = await jobAppService.CreateAsync(request, userName);
        return AsHttpResult(AgwApiResult.Ok(job));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        JobUpdateRequest request,
        JobAppService jobAppService,
        ClaimsPrincipal user)
    {
        var userName = user.Identity?.Name ?? "system";
        var job = await jobAppService.UpdateAsync(id, request, userName);
        return AsHttpResult(job == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(job));
    }

    private static async Task<IResult> DeleteAsync(Guid id, JobAppService jobAppService)
    {
        var deleted = await jobAppService.DeleteAsync(id);
        return AsHttpResult(deleted ? AgwApiResult.Ok() : AgwApiResult.NotFound());
    }

    private static IResult AsHttpResult(IActionResult actionResult)
    {
        return (IResult)actionResult;
    }
}
