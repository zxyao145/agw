using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Results;
using Agw.Tasks.Application;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Tasks.Controllers;

[ApiController]
[Route("api/projects/{projectId}/tasks")]
public class ProjectTasksController : ControllerBase
{
    private readonly ProjectTaskAppService _projectTaskAppService;

    public ProjectTasksController(ProjectTaskAppService projectTaskAppService)
    {
        _projectTaskAppService = projectTaskAppService;
    }

    [HttpGet]
    [ProducesApiResult(typeof(ProjectTaskSummaryResponse[]))]
    public async Task<IActionResult> ListAsync(Guid projectId) => AgwApiResult.Ok(await _projectTaskAppService.ListResponsesAsync(projectId));

    [HttpGet("{taskId:guid}")]
    [ProducesApiResult(typeof(ProjectTaskResponse))]
    public async Task<IActionResult> GetAsync(Guid projectId, Guid taskId)
    {
        var task = await _projectTaskAppService.GetResponseAsync(projectId, taskId);
        return task == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(task);
    }

    [HttpPut("{taskId:guid}/title")]
    [ProducesApiResult]
    public async Task<IActionResult> UpdateTitleAsync(
        Guid projectId,
        Guid taskId,
        [FromBody] ProjectTaskTitleUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var result = await _projectTaskAppService.UpdateTitleAsync(projectId, taskId, request.Title, user);

        return result.Type switch
        {
            ApplicationResultType.Success => AgwApiResult.Ok(),
            ApplicationResultType.NotFound => AgwApiResult.NotFound(),
            ApplicationResultType.Invalid => AgwApiResult.BadRequest(result.Error ?? "Invalid request."),
            _ => AgwApiResult.BadRequest(result.Error ?? "Invalid request.")
        };
    }

    [HttpDelete("{taskId:guid}")]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(Guid projectId, Guid taskId)
    {
        var result = await _projectTaskAppService.DeleteTaskAsync(projectId, taskId);
        return result.Type == ApplicationResultType.Success ? AgwApiResult.Ok() : AgwApiResult.NotFound();
    }

    [HttpDelete("{taskId:guid}/clear-records")]
    [ProducesApiResult]
    public async Task<IActionResult> ClearRecordsAsync(Guid projectId, Guid taskId)
    {
        var result = await _projectTaskAppService.ClearRecordsAsync(projectId, taskId);
        return result.Type == ApplicationResultType.Success ? AgwApiResult.Ok() : AgwApiResult.NotFound();
    }
}
