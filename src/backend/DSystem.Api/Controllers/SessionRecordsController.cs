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
        var recordsBySession = records
            .GroupBy(r => r.SessionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var taskBySession = (await GetTasksByProjectIdAsync(projectId))
            .GroupBy(t => t.SessionId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.UpdateTime ?? t.CreateTime).First());

        var sessionIds = recordsBySession.Keys.Union(taskBySession.Keys).ToList();
        var summaries = sessionIds
            .Select(sessionId => CreateSummary(projectId, sessionId, recordsBySession.GetValueOrDefault(sessionId), taskBySession.GetValueOrDefault(sessionId)))
            .OrderByDescending(s => s.UpdateTime ?? s.CreateTime)
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
        var orderedRecords = records
            .OrderBy(r => r.CreateTime)
            .ThenBy(r => r.UpdateTime ?? r.CreateTime)
            .ToList();
        var messages = orderedRecords.Select(ToAiMessage).ToList();

        var createTime = task?.CreateTime ?? orderedRecords.First().CreateTime;
        var updateTime = task?.UpdateTime ?? orderedRecords.Last().UpdateTime ?? orderedRecords.Last().CreateTime;
        var response = new SessionRecordDetails(
            task?.Id ?? TryParseSessionIdAsGuid(sessionId),
            projectId,
            task?.SessionId ?? sessionId,
            NormalizeTitle(task?.Title),
            messages,
            createTime,
            updateTime);

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
            record.Metadata?.ToDictionary(x => x.Key, x => (object?)x.Value)
            );

    private static string NormalizeTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "New Chat" : title;

    private static SessionRecordSummary CreateSummary(
        string projectId,
        string sessionId,
        List<AgentSessionRecord>? records,
        ProjectTask? task)
    {
        var sessionRecords = records ?? [];
        var orderedRecords = sessionRecords
            .OrderBy(r => r.CreateTime)
            .ThenBy(r => r.UpdateTime ?? r.CreateTime)
            .ToList();

        var createTime = task?.CreateTime
            ?? orderedRecords.FirstOrDefault()?.CreateTime
            ?? DateTime.UtcNow;

        var updateTime = task?.UpdateTime
            ?? orderedRecords.LastOrDefault()?.UpdateTime
            ?? orderedRecords.LastOrDefault()?.CreateTime;

        return new SessionRecordSummary(
            task?.Id ?? TryParseSessionIdAsGuid(sessionId),
            projectId,
            task?.SessionId ?? sessionId,
            NormalizeTitle(task?.Title),
            sessionRecords.Count,
            createTime,
            updateTime);
    }

    private static Guid TryParseSessionIdAsGuid(string sessionId) =>
        Guid.TryParse(sessionId, out var parsed) ? parsed : Guid.Empty;
}
