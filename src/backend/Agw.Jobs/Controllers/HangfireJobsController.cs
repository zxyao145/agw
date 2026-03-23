using Agw.Jobs.Contracts;
using Agw.Jobs.Services;
using Agw.Tasks.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Jobs.Controllers;

[ApiController]
[Route("api/hangfire/jobs")]
public sealed class HangfireJobsController : ControllerBase
{
    private readonly IHangfireJobAppService _hangfireJobAppService;

    public HangfireJobsController(IHangfireJobAppService hangfireJobAppService)
    {
        _hangfireJobAppService = hangfireJobAppService;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        var jobs = await _hangfireJobAppService.ListAsync(cancellationToken);
        return Ok(jobs);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await _hangfireJobAppService.GetAsync(id, cancellationToken);
        return job == null ? NotFound() : Ok(job);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] HangfireJobUpsertRequest request, CancellationToken cancellationToken)
    {
        var user = User?.Identity?.Name ?? "system";
        var result = await _hangfireJobAppService.CreateAsync(request, user, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] HangfireJobUpsertRequest request, CancellationToken cancellationToken)
    {
        var user = User?.Identity?.Name ?? "system";
        var result = await _hangfireJobAppService.UpdateAsync(id, request, user, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("{id:guid}/pause")]
    public async Task<IActionResult> PauseAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = User?.Identity?.Name ?? "system";
        var result = await _hangfireJobAppService.PauseAsync(id, user, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("{id:guid}/start")]
    public async Task<IActionResult> StartAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = User?.Identity?.Name ?? "system";
        var result = await _hangfireJobAppService.StartAsync(id, user, cancellationToken);
        return ToActionResult(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await _hangfireJobAppService.DeleteAsync(id, cancellationToken);
        return result.Type switch
        {
            ApplicationResultType.Success => NoContent(),
            ApplicationResultType.NotFound => NotFound(),
            _ => BadRequest(result.Error ?? "Failed to delete Hangfire job.")
        };
    }

    private static IActionResult ToActionResult(ApplicationResult<HangfireJobDetailResponse> result)
    {
        return result.Type switch
        {
            ApplicationResultType.Success when result.Value != null => new OkObjectResult(result.Value),
            ApplicationResultType.NotFound => new NotFoundResult(),
            _ => new BadRequestObjectResult(result.Error ?? "Operation failed.")
        };
    }
}
