using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Shared.Contracts;
using DSystem.Shared.Enums;
using Microsoft.AspNetCore.Mvc;

namespace DSystem.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId:guid}/tasks")]
public class ProjectTasksController : ControllerBase
{
    private readonly ProjectTaskDomainService _taskService;

    public ProjectTasksController(ProjectTaskDomainService taskService)
    {
        _taskService = taskService;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync(Guid projectId)
    {
        var projectIdText = projectId.ToString("D");
        var tasks = await _taskService.ListAsync(t => t.ProjectId == projectIdText);
        return Ok(tasks.OrderByDescending(t => t.CreateTime).ToList());
    }

    [HttpGet("{taskId:guid}")]
    public async Task<IActionResult> GetAsync(Guid projectId, Guid taskId)
    {
        var task = await _taskService.GetAsync(taskId);
        if (task == null || task.ProjectId != projectId.ToString("D"))
        {
            return NotFound();
        }

        return Ok(ToResponse(task));
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync(Guid projectId, [FromBody] ProjectTaskCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";

        var task = new ProjectTask
        {
            ProjectId = projectId.ToString("D"),
            AgentType = request.AgentType,
            AgentflowId = request.AgentflowId,
            AgentId = request.AgentId,
            SessionId = request.SessionId,
            Title = request.Title,
            Description = request.Description,
            Input = request.Input,
            Status = ProjectTaskStatus.Pending
        };

        var created = await _taskService.CreateAsync(task, user);
        if (created == null)
        {
            return BadRequest("Failed to create task (project/target invalid, target mismatch, or input missing).");
        }

        // Asynchronous execution: scheduler will pick it up.
        return Accepted(ToResponse(created));
    }

    [HttpPut("{taskId:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid projectId, Guid taskId, [FromBody] ProjectTaskUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";

        var existing = await _taskService.GetAsync(taskId);
        if (existing == null || existing.ProjectId != projectId.ToString("D"))
        {
            return NotFound();
        }

        if (existing.Status != ProjectTaskStatus.Pending)
        {
            return BadRequest("Only pending tasks can be updated.");
        }

        var updated = await _taskService.UpdateAsync(taskId, t =>
        {
            t.Description = request.Description;
            t.Input = request.Input;
        }, user);

        return updated == null ? BadRequest("Failed to update task.") : Ok(ToResponse(updated));
    }

    [HttpPost("{taskId:guid}/reorder")]
    public async Task<IActionResult> ReorderAsync(Guid projectId, Guid taskId, [FromBody] ProjectTaskReorderRequest request)
    {
        var user = User?.Identity?.Name ?? "system";

        var existing = await _taskService.GetAsync(taskId);
        if (existing == null || existing.ProjectId != projectId.ToString("D"))
        {
            return NotFound();
        }

        var updated = await _taskService.ReorderAsync(taskId, request.UpdateTimeUtc, user);
        return updated == null ? BadRequest("Only pending tasks can be reordered.") : Ok(ToResponse(updated));
    }

    [HttpPost("{taskId:guid}/cancel")]
    public async Task<IActionResult> CancelAsync(Guid projectId, Guid taskId)
    {
        var user = User?.Identity?.Name ?? "system";

        var existing = await _taskService.GetAsync(taskId);
        if (existing == null || existing.ProjectId != projectId.ToString("D"))
        {
            return NotFound();
        }

        var canceled = await _taskService.CancelAsync(taskId, user);
        return canceled == null ? BadRequest("Task cannot be canceled in its current state.") : Ok(ToResponse(canceled));
    }


    [HttpDelete("{taskId:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid projectId, Guid taskId)
    {
        var user = User?.Identity?.Name ?? "system";

        var existing = await _taskService.GetAsync(taskId);
        if (existing == null || existing.ProjectId != projectId.ToString("D"))
        {
            return NotFound();
        }

        await _taskService.DeleteAsync(taskId, user);
        return Ok();
    }


    private static ProjectTaskResponse ToResponse(ProjectTask task) =>
        new(
            task.Id,
            task.ProjectId,
            task.AgentType,
            task.AgentflowId,
            task.AgentId,
            task.Status,
            task.SessionId,
            task.Title,
            task.Description,
            task.Input,
            task.ErrorMessage,
            task.CreateTime,
            task.UpdateTime,
            task.StartedTime,
            task.FinishedTime);
}
