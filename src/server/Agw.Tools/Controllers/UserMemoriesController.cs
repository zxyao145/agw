using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Agw.Tools.Application;
using Agw.Tools.Contracts.UserMemories;

using Bens.Results;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Tools.Controllers;

[Authorize]
[ApiController]
[Route("api/user-memories")]
public sealed class UserMemoriesController : ControllerBase
{
    private readonly UserMemoryAppService _appService;

    public UserMemoriesController(UserMemoryAppService appService)
    {
        _appService = appService;
    }

    [HttpGet("paged")]
    [ProducesApiResult(typeof(PagedResult<UserMemorySummaryResponse>))]
    public async Task<IActionResult> ListPagedAsync(
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var page = await _appService.ListPageAsync(pageIndex, pageSize, cancellationToken);
        return ApiResult.Ok(new PagedResult<UserMemorySummaryResponse>
        {
            Items = page.Items.Select(MapSummary).ToList(),
            Total = page.Total,
            PageIndex = page.PageIndex,
            PageSize = page.PageSize
        });
    }

    [HttpGet("detail")]
    [ProducesApiResult(typeof(UserMemoryDetailResponse))]
    public async Task<IActionResult> GetAsync(
        [FromQuery] Guid id,
        CancellationToken cancellationToken = default)
    {
        var memory = await _appService.GetAsync(id, cancellationToken);
        return memory == null
            ? ErrorCodes.ResourceNotFound.ToApiResult()
            : ApiResult.Ok(MapDetails(memory));
    }

    [HttpPost]
    [ProducesApiResult(typeof(UserMemoryDetailResponse))]
    public async Task<IActionResult> CreateAsync(
        [FromBody] UserMemoryCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var memory = await _appService.CreateAsync(
            request.Name,
            request.Description,
            request.Content,
            cancellationToken);
        return ApiResult.Ok(MapDetails(memory));
    }

    [HttpPut]
    [ProducesApiResult(typeof(UserMemoryDetailResponse))]
    public async Task<IActionResult> UpdateAsync(
        [FromBody] UserMemoryUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var memory = await _appService.UpdateAsync(
            request.Id,
            request.Name,
            request.Description,
            request.Content,
            cancellationToken);
        return memory == null
            ? ErrorCodes.ResourceNotFound.ToApiResult()
            : ApiResult.Ok(MapDetails(memory));
    }

    [HttpDelete]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(
        [FromQuery] Guid id,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _appService.DeleteAsync(id, cancellationToken);
        return deleted ? ApiResult.Ok() : ErrorCodes.ResourceNotFound.ToApiResult();
    }

    private static UserMemorySummaryResponse MapSummary(UserMemorySummary memory) =>
        new(
            memory.Id,
            memory.Name,
            memory.Description,
            memory.CreateTime,
            memory.UpdateTime);

    private static UserMemoryDetailResponse MapDetails(UserMemoryDetails memory) =>
        new(
            memory.Id,
            memory.Name,
            memory.Description,
            memory.Content,
            memory.CreateTime,
            memory.UpdateTime);
}
