using Agw.Projects.Application;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;

using Bens.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Projects.Controllers;

[ApiController]
[Route("api/projects/{projectId}/contexts")]
public class ProjectContextsController : ControllerBase
{
    private readonly ProjectContextAppService _projectContextAppService;

    public ProjectContextsController(ProjectContextAppService projectContextAppService)
    {
        _projectContextAppService = projectContextAppService;
    }

    [HttpGet]
    [ProducesApiResult(typeof(ProjectContextSummaryResponse[]))]
    public async Task<IActionResult> ListAsync(Guid projectId) =>
        ApiResult.Ok(await _projectContextAppService.ListResponsesAsync(projectId));

    [HttpGet("{contextId}")]
    [ProducesApiResult(typeof(ProjectContextResponse))]
    public async Task<IActionResult> GetAsync(Guid projectId, string contextId)
    {
        var context = await _projectContextAppService.GetResponseAsync(projectId, contextId);
        return context == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(context);
    }

    [HttpDelete("{contextId}/clear-records")]
    [ProducesApiResult]
    public async Task<IActionResult> ClearRecordsAsync(Guid projectId, string contextId)
    {
        var result = await _projectContextAppService.ClearRecordsAsync(projectId, contextId);
        return result.Type == ApplicationResultType.Success ? ApiResult.Ok() : ErrorCodes.ResourceNotFound.ToApiResult();
    }

    [HttpPut("{contextId}/title")]
    [ProducesApiResult]
    public async Task<IActionResult> UpdateTitleAsync(
        Guid projectId,
        string contextId,
        [FromBody] ProjectContextTitleUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var result = await _projectContextAppService.UpdateTitleAsync(projectId, contextId, request.Title, user);

        return result.Type switch
        {
            ApplicationResultType.Success => ApiResult.Ok(),
            ApplicationResultType.NotFound => ErrorCodes.ResourceNotFound.ToApiResult(),
            ApplicationResultType.Invalid => ApiResult.BadRequest(
                result.Error ?? "Invalid request.",
                ErrorCodes.InvalidParam.Code),
            _ => ApiResult.BadRequest(
                result.Error ?? "Invalid request.",
                ErrorCodes.InvalidParam.Code)
        };
    }

    [HttpDelete]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAllAsync(Guid projectId)
    {
        var result = await _projectContextAppService.DeleteAllAsync(projectId);
        return result.Type == ApplicationResultType.Success ? ApiResult.Ok() : ErrorCodes.ResourceNotFound.ToApiResult();
    }

    [HttpDelete("{contextId}")]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(Guid projectId, string contextId)
    {
        var deleted = await _projectContextAppService.DeleteAsync(projectId, contextId);
        return deleted ? ApiResult.Ok() : ErrorCodes.ResourceNotFound.ToApiResult();
    }
}
