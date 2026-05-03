using Agw.Jobs.Application.Services;
using Agw.Jobs.Contracts;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Jobs.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController(JobAppService jobAppService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        var tasks = await jobAppService.ListAsync(cancellationToken);
        return AgwApiResult.Ok(tasks);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var task = await jobAppService.GetAsync(id);
        return task == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(task);
    }

    [HttpGet("{id:guid}/logs")]
    public async Task<IActionResult> ListLogsAsync(Guid id, CancellationToken cancellationToken)
    {
        var logs = await jobAppService.ListLogsAsync(id, cancellationToken);
        return AgwApiResult.Ok(logs);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] JobCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var task = await jobAppService.CreateAsync(request, user);
        return AgwApiResult.Ok(task);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] JobUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var task = await jobAppService.UpdateAsync(id, request, user);
        return task == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(task);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await jobAppService.DeleteAsync(id);
        return deleted ? AgwApiResult.Ok() : AgwApiResult.NotFound();
    }
}
