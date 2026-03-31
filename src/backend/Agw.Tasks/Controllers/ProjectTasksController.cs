using Agw.Shared.Contracts;
using Agw.Tasks.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Api.Controllers;

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
    public async Task<IActionResult> ListAsync(Guid projectId)
    {
        var tasks = await _projectTaskAppService.ListResponsesAsync(projectId);
        return Ok(tasks);
    }

    [HttpGet("{taskId:guid}")]
    public async Task<IActionResult> GetAsync(Guid projectId, Guid taskId)
    {
        var task = await _projectTaskAppService.GetResponseAsync(projectId, taskId);
        return task == null ? NotFound() : Ok(task);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync(Guid projectId, [FromBody] ProjectTaskCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var result = await _projectTaskAppService.CreateAsync(projectId, request, user);
        if (result.Type != ApplicationResultType.Success || result.Value == null)
        {
            return BadRequest(result.Error ?? "Failed to create task (project/target invalid, target mismatch, or input missing).");
        }

        return Accepted(result.Value);
    }

    [HttpPut("{taskId:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid projectId, Guid taskId, [FromBody] ProjectTaskUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var result = await _projectTaskAppService.UpdateAsync(projectId, taskId, request, user);
        return result.Type switch
        {
            ApplicationResultType.Success when result.Value != null => Ok(result.Value),
            ApplicationResultType.NotFound => NotFound(),
            _ => BadRequest(result.Error ?? "Failed to update task.")
        };
    }


    [HttpDelete("{taskId:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid projectId, Guid taskId)
    {
        var result = await _projectTaskAppService.DeleteAsync(projectId, taskId);
        return result.Type == ApplicationResultType.Success ? Ok() : NotFound();
    }

    [HttpDelete("{taskId:guid}/session")]
    public async Task<IActionResult> ClearSessionAsync(Guid projectId, Guid taskId)
    {
        var result = await _projectTaskAppService.DeleteSessionAsync(projectId, taskId);
        return result.Type == ApplicationResultType.Success ? NoContent() : NotFound();
    }


    [HttpPut("{taskId:guid}/title")]
    public async Task<IActionResult> UpdateTitleAsync(
        Guid projectId,
        Guid taskId,
        [FromBody] SessionRecordTitleUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var result = await _projectTaskAppService.UpdateTitleAsync(projectId, taskId, request.Title, user);
        return result.Type switch
        {
            ApplicationResultType.Success => NoContent(),
            ApplicationResultType.NotFound => NotFound(),
            _ => BadRequest(result.Error ?? "title is required.")
        };
    }


    [HttpPost("{taskId:guid}/cancel")]
    public async Task<IActionResult> CancelAsync(Guid projectId, Guid taskId)
    {
        var user = User?.Identity?.Name ?? "system";
        var result = await _projectTaskAppService.CancelAsync(projectId, taskId, user);
        return result.Type switch
        {
            ApplicationResultType.Success when result.Value != null => Ok(result.Value),
            ApplicationResultType.NotFound => NotFound(),
            _ => BadRequest(result.Error ?? "Task cannot be canceled in its current state.")
        };
    }



    [HttpPost("{taskId:guid}/reorder")]
    public async Task<IActionResult> ReorderAsync(Guid projectId, Guid taskId, [FromBody] ProjectTaskReorderRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var result = await _projectTaskAppService.ReorderAsync(projectId, taskId, request.UpdateTimeUtc, user);
        return result.Type switch
        {
            ApplicationResultType.Success when result.Value != null => Ok(result.Value),
            ApplicationResultType.NotFound => NotFound(),
            _ => BadRequest(result.Error ?? "Only pending tasks can be reordered.")
        };
    }
}
