using Agw.Shared.Contracts.Tasks;
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
    public async Task<IActionResult> ListAsync(Guid projectId) => Ok(await _projectTaskAppService.ListResponsesAsync(projectId));

    [HttpGet("{taskId:guid}")]
    public async Task<IActionResult> GetAsync(Guid projectId, Guid taskId)
    {
        var task = await _projectTaskAppService.GetResponseAsync(projectId, taskId);
        return task == null ? NotFound() : Ok(task);
    }

    [HttpPut("{taskId:guid}/title")]
    public async Task<IActionResult> UpdateTitleAsync(
        Guid projectId,
        Guid taskId,
        [FromBody] ProjectTaskTitleUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var result = await _projectTaskAppService.UpdateTitleAsync(projectId, taskId, request.Title, user);

        return result.Type switch
        {
            ApplicationResultType.Success => Ok(),
            ApplicationResultType.NotFound => NotFound(),
            ApplicationResultType.Invalid => BadRequest(result.Error),
            _ => BadRequest(result.Error)
        };
    }

    [HttpDelete("{taskId:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid projectId, Guid taskId)
    {
        var result = await _projectTaskAppService.DeleteTaskAsync(projectId, taskId);
        return result.Type == ApplicationResultType.Success ? Ok() : NotFound();
    }

    [HttpDelete("{taskId:guid}/clear-records")]
    public async Task<IActionResult> ClearRecordsAsync(Guid projectId, Guid taskId)
    {
        var result = await _projectTaskAppService.ClearRecordsAsync(projectId, taskId);
        return result.Type == ApplicationResultType.Success ? Ok() : NotFound();
    }
}
