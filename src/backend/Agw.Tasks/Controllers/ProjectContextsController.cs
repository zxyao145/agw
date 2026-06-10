using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Results;
using Agw.Tasks.Application;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Tasks.Controllers;

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
        AgwApiResult.Ok(await _projectContextAppService.ListResponsesAsync(projectId));

    [HttpGet("{contextId}")]
    [ProducesApiResult(typeof(ProjectContextResponse))]
    public async Task<IActionResult> GetAsync(Guid projectId, string contextId)
    {
        var context = await _projectContextAppService.GetResponseAsync(projectId, contextId);
        return context == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(context);
    }

    [HttpGet("by-task/{taskId:guid}")]
    [ProducesApiResult(typeof(ProjectContextResponse))]
    public async Task<IActionResult> GetByTaskIdAsync(Guid projectId, Guid taskId)
    {
        var context = await _projectContextAppService.GetResponseByTaskIdAsync(projectId, taskId);
        return context == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(context);
    }

    [HttpDelete("{contextId}/clear-records")]
    [ProducesApiResult]
    public async Task<IActionResult> ClearRecordsAsync(Guid projectId, string contextId)
    {
        var result = await _projectContextAppService.ClearRecordsAsync(projectId, contextId);
        return result.Type == ApplicationResultType.Success ? AgwApiResult.Ok() : AgwApiResult.NotFound();
    }

    [HttpPut("{contextId}/title")]
    [ProducesApiResult]
    public async Task<IActionResult> UpdateTitleAsync(
        Guid projectId,
        string contextId,
        [FromBody] ProjectTaskTitleUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var result = await _projectContextAppService.UpdateTitleAsync(projectId, contextId, request.Title, user);

        return result.Type switch
        {
            ApplicationResultType.Success => AgwApiResult.Ok(),
            ApplicationResultType.NotFound => AgwApiResult.NotFound(),
            ApplicationResultType.Invalid => AgwApiResult.BadRequest(result.Error ?? "Invalid request."),
            _ => AgwApiResult.BadRequest(result.Error ?? "Invalid request.")
        };
    }

    [HttpDelete]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAllAsync(Guid projectId)
    {
        var result = await _projectContextAppService.DeleteAllAsync(projectId);
        return result.Type == ApplicationResultType.Success ? AgwApiResult.Ok() : AgwApiResult.NotFound();
    }

    [HttpDelete("{contextId}")]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(Guid projectId, string contextId)
    {
        var deleted = await _projectContextAppService.DeleteAsync(projectId, contextId);
        return deleted ? AgwApiResult.Ok() : AgwApiResult.NotFound();
    }
}
