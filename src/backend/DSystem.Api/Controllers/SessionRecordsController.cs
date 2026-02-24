using DSystem.SessionRecords.Domain;
using DSystem.SessionRecords.Entities;
using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;
using DSystem.Shared;
using DSystem.Shared.Contracts;
using DSystem.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace DSystem.Api.Controllers;

[ApiController]
[Route("api/session-records")]
public class SessionRecordsController : ControllerBase
{
    private readonly SessionRecordDomainService _service;
    private readonly IRepository<ProjectTask> _taskRepository;

    public SessionRecordsController(SessionRecordDomainService service, IRepository<ProjectTask> taskRepository)
    {
        _service = service;
        _taskRepository = taskRepository;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync([FromQuery] string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return BadRequest("projectId is required.");
        }

        var records = await _service.ListAsync(r => r.ProjectId == projectId);
        var messageCounts = records
            .GroupBy(r => r.SessionId)
            .ToDictionary(g => g.Key, g => g.Count());

        var tasks = await GetTasksByProjectIdAsync(projectId);

        var summaries = tasks
            .OrderByDescending(t => t.UpdateTime ?? t.CreateTime)
            .Select(t => new SessionRecordSummary(
                t.Id,
                projectId,
                t.SessionId,
                NormalizeTitle(t.Title),
                messageCounts.GetValueOrDefault(t.SessionId),
                t.CreateTime,
                t.UpdateTime))
            .ToList();

        return Ok(summaries);
    }

    [HttpGet("{sessionId}")]
    public async Task<IActionResult> GetAsync(string sessionId, [FromQuery] string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return BadRequest("projectId is required.");
        }

        var records = await _service.GetBySessionIdAsync(sessionId, projectId);
        if (records.Count == 0)
        {
            return NotFound();
        }

        var task = (await GetTasksByProjectIdAsync(projectId)).FirstOrDefault(t => t.SessionId == sessionId);
        if (task == null)
        {
            return NotFound();
        }

        var messages = records.Select(ToAiMessage).ToList();
        var response = new SessionRecordDetails(
            task.Id,
            projectId,
            task.SessionId,
            NormalizeTitle(task.Title),
            messages,
            task.CreateTime,
            task.UpdateTime ?? task.CreateTime);

        return Ok(response);
    }

    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> DeleteAsync(string sessionId, [FromQuery] string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return BadRequest("projectId is required.");
        }

        var deleted = await _service.DeleteBySessionIdAsync(sessionId, projectId);
        return deleted ? NoContent() : NotFound();
    }

    private async Task<IReadOnlyList<ProjectTask>> GetTasksByProjectIdAsync(string projectId)
    {
        return await _taskRepository.ListAsync(t => t.ProjectId == projectId);
    }

    private static AiMessage ToAiMessage(AgentSessionRecord record) =>
        new(
            record.MessageId,
            record.Author,
            record.Role,
            record.Contents,
            record.Metadata?.ToDictionary(x => x.Key, x => (object?)x.Value));

    private static string NormalizeTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "New Chat" : title;
}
