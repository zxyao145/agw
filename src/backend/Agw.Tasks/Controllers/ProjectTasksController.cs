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

    [HttpDelete("{taskId:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid projectId, Guid taskId)
    {
        var result = await _projectTaskAppService.DeleteAsync(projectId, taskId);
        return result.Type == ApplicationResultType.Success ? Ok() : NotFound();
    }
}
