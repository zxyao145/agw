using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Shared;
using DSystem.Shared.Contracts;
using DSystem.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace DSystem.Api.Controllers;

[ApiController]
[Route("api/session-records")]
public class SessionRecordsController : ControllerBase
{
    private readonly TaskRecordDomainService _recordService;
    private readonly ProjectTaskDomainService _taskService;

    public SessionRecordsController(
        TaskRecordDomainService recordService,
        ProjectTaskDomainService taskService)
    {
        _recordService = recordService;
        _taskService = taskService;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync([FromQuery] string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return BadRequest("projectId is required.");
        }

        var tasks = await _taskService.ListAsync(t => t.ProjectId == projectId);
        if (tasks.Count == 0)
        {
            return Ok(Array.Empty<SessionRecordSummary>());
        }

        var records = await _recordService.GetByContextIdsAsync(tasks.Select(t => t.ContextId));
        var recordsByContext = records
            .GroupBy(record => record.ContextId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var summaries = tasks
            .Where(task => recordsByContext.ContainsKey(task.ContextId))
            .Select(task => CreateSummary(task, recordsByContext[task.ContextId]))
            .OrderByDescending(summary => summary.UpdateTime ?? summary.CreateTime)
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

        var task = await _recordService.FindTaskAsync(sessionId, projectId);
        if (task == null)
        {
            return NotFound();
        }

        var records = await _recordService.GetByContextIdAsync(task.ContextId);
        if (records.Count == 0)
        {
            return NotFound();
        }

        var orderedRecords = records
            .OrderBy(record => record.CreateTime)
            .ThenBy(record => record.UpdateTime ?? record.CreateTime)
            .ToList();
        var messages = orderedRecords
            .SelectMany(ToAiMessages)
            .ToList();

        var updateTime = task.UpdateTime
            ?? orderedRecords.Last().UpdateTime
            ?? orderedRecords.Last().CreateTime;

        var response = new SessionRecordDetails(
            task.Id,
            projectId,
            task.ContextId,
            NormalizeTitle(task.Title),
            messages,
            task.CreateTime,
            updateTime);

        return Ok(response);
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
        var task = await _recordService.UpdateTaskTitleAsync(sessionId, projectId, request.Title, user);
        return task == null ? NotFound() : NoContent();
    }

    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> DeleteAsync(string sessionId, [FromQuery] string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return BadRequest("projectId is required.");
        }

        var deleted = await _recordService.DeleteBySessionIdAsync(sessionId, projectId);
        return deleted ? NoContent() : NotFound();
    }

    private static IEnumerable<AiMessage> ToAiMessages(TaskRecord record)
    {
        var message = record.ToChatMessage()?.ToAiMessage();
        if (message != null)
        {
            yield return message;
        }
    }

    private static string NormalizeTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "New Chat" : title;

    private static SessionRecordSummary CreateSummary(ProjectTask task, List<TaskRecord> records)
    {
        var orderedRecords = records
            .OrderBy(record => record.CreateTime)
            .ThenBy(record => record.UpdateTime ?? record.CreateTime)
            .ToList();

        var updateTime = task.UpdateTime
            ?? orderedRecords.LastOrDefault()?.UpdateTime
            ?? orderedRecords.LastOrDefault()?.CreateTime;
        var messageCount = orderedRecords.Sum(CountMessages);

        return new SessionRecordSummary(
            task.Id,
            task.ProjectId,
            task.ContextId,
            NormalizeTitle(task.Title),
            messageCount,
            task.CreateTime,
            updateTime);
    }

    private static int CountMessages(TaskRecord record) =>
        record.ToChatMessage() == null ? 0 : 1;
}
