using Agw.Jobs.Contracts;
using Agw.Jobs.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Api.Controllers;

[ApiController]
[Route("api/scheduled-tasks")]
public class ScheduledTasksController(ScheduledTaskAppService scheduledTaskAppService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        var tasks = await scheduledTaskAppService.ListAsync(cancellationToken);
        return Ok(tasks);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var task = await scheduledTaskAppService.GetAsync(id);
        return task == null ? NotFound() : Ok(task);
    }

    [HttpGet("{id:guid}/logs")]
    public async Task<IActionResult> ListLogsAsync(Guid id, CancellationToken cancellationToken)
    {
        var logs = await scheduledTaskAppService.ListLogsAsync(id, cancellationToken);
        return Ok(logs);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] ScheduledTaskCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var task = await scheduledTaskAppService.CreateAsync(request, user);
        return Ok(task);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ScheduledTaskUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var task = await scheduledTaskAppService.UpdateAsync(id, request, user);
        return task == null ? NotFound() : Ok(task);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await scheduledTaskAppService.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
