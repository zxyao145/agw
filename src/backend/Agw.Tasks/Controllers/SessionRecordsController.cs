using Agw.Shared.Contracts;
using Agw.Tasks.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Api.Controllers;

[ApiController]
[Route("api/session-records")]
public class SessionRecordsController : ControllerBase
{
    private readonly SessionRecordAppService _sessionRecordAppService;

    public SessionRecordsController(SessionRecordAppService sessionRecordAppService)
    {
        _sessionRecordAppService = sessionRecordAppService;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync([FromQuery] string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return BadRequest("projectId is required.");
        }

        var summaries = await _sessionRecordAppService.ListAsync(projectId);
        return Ok(summaries);
    }

    [HttpGet("{sessionId}")]
    public async Task<IActionResult> GetAsync(string sessionId, [FromQuery] string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return BadRequest("projectId is required.");
        }

        var session = await _sessionRecordAppService.GetAsync(sessionId, projectId);
        return session == null ? NotFound() : Ok(session);
    }

    [HttpPut("{sessionId}/title")]
    public async Task<IActionResult> UpdateTitleAsync(
        string sessionId,
        [FromQuery] string projectId,
        [FromBody] SessionRecordTitleUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return BadRequest("projectId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest("title is required.");
        }

        var user = User?.Identity?.Name ?? "system";
        var result = await _sessionRecordAppService.UpdateTitleAsync(sessionId, projectId, request.Title, user);
        return result.Type switch
        {
            ApplicationResultType.Success => NoContent(),
            ApplicationResultType.NotFound => NotFound(),
            _ => BadRequest(result.Error ?? "title is required.")
        };
    }

    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> DeleteAsync(string sessionId, [FromQuery] string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return BadRequest("projectId is required.");
        }

        var result = await _sessionRecordAppService.DeleteAsync(sessionId, projectId);
        return result.Type == ApplicationResultType.Success ? NoContent() : NotFound();
    }
}
