using Agw.Domain.Services;
using Agw.Shared;
using Agw.Shared.Contracts;
using Agw.Shared.Enums;
using Agw.Shared.Models;
using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.Mvc;
using Agw.Shared.Tasks.Entities;

namespace Agw.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId}/tasks")]
public class ProjectTasksController : ControllerBase
{
    private readonly ProjectTaskDomainService _taskService;
    private readonly TaskRecordDomainService _taskRecordService;

    public ProjectTasksController(
        ProjectTaskDomainService taskService,
        TaskRecordDomainService taskRecordService)
    {
        _taskService = taskService;
        _taskRecordService = taskRecordService;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync(string projectId)
    {
        var normalizedProjectId = NormalizeProjectId(projectId);
        var tasks = await _taskService.ListAsync(t => t.ProjectId == normalizedProjectId);
        var records = await _taskRecordService.GetByContextIdsAsync(tasks.Select(t => t.ContextId));
        var recordsByContext = records
            .GroupBy(record => record.ContextId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var responses = tasks
            .OrderByDescending(t => t.UpdateTime ?? t.CreateTime)
            .Select(task =>
            {
                var taskRecords = recordsByContext.GetValueOrDefault(task.ContextId) ?? [];
                return ToResponse(
                    task,
                    taskRecords,
                    null);
            })
            .ToList();

        return Ok(responses);
    }

    [HttpGet("{taskId:guid}")]
    public async Task<IActionResult> GetAsync(string projectId, Guid taskId)
    {
        var normalizedProjectId = NormalizeProjectId(projectId);
        var task = await _taskService.GetAsync(taskId);
        if (task == null || task.ProjectId != normalizedProjectId)
        {
            return NotFound();
        }

        var records = await _taskRecordService.GetByContextIdAsync(task.ContextId);
        var messages = records.SelectMany(ToAiMessages).ToList();
        return Ok(ToResponse(task, records, messages));
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync(string projectId, [FromBody] ProjectTaskCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var normalizedProjectId = NormalizeProjectId(projectId);
        var taskId = Guid.NewGuid();
        var contextId = string.IsNullOrWhiteSpace(request.ContextId)
            ? taskId.Normalize()
            : request.ContextId.Trim();
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? contextId
            : request.SessionId.Trim();

        var task = new ProjectTask
        {
            Id = taskId,
            ProjectId = normalizedProjectId,
            ContextId = contextId,
            AgentType = request.AgentType,
            AgentId = request.AgentType == ProjectTaskAgentType.Agentflow
                ? request.AgentflowId
                : request.AgentId,
            Title = request.Title ?? string.Empty,
            Description = request.Description,
            SystemPrompt = request.SystemPrompt,
            Status = ProjectTaskStatus.Pending
        };

        var inputMsg = new ChatMessage(ChatRole.User, request.Input.Trim())
        {
            MessageId = Guid.NewGuid().ToString(),
            AuthorName = Constants.DefaultAuthor
        };

        var initialRecord = new TaskRecord
        {
            Id = Guid.NewGuid(),
            ContextId = contextId,
            SessionId = sessionId,
            ConversationSequence = 0,
            ConversationPayload = JsonUtil.Serialize(inputMsg)
        };

        var created = await _taskService.CreateAsync(task, initialRecord, user);
        if (created == null)
        {
            return BadRequest("Failed to create task (project/target invalid, target mismatch, or input missing).");
        }

        return Accepted(ToResponse(created, [initialRecord], null));
    }

    [HttpPut("{taskId:guid}")]
    public async Task<IActionResult> UpdateAsync(string projectId, Guid taskId, [FromBody] ProjectTaskUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var normalizedProjectId = NormalizeProjectId(projectId);

        var existing = await _taskService.GetAsync(taskId);
        if (existing == null || existing.ProjectId != normalizedProjectId)
        {
            return NotFound();
        }

        if (existing.Status != ProjectTaskStatus.Pending)
        {
            return BadRequest("Only pending tasks can be updated.");
        }

        var updated = await _taskService.UpdateAsync(taskId, request.Description, request.Input, user);
        if (updated == null)
        {
            return BadRequest("Failed to update task.");
        }

        var records = await _taskRecordService.GetByContextIdAsync(updated.ContextId);
        return Ok(ToResponse(updated, records, null));
    }

    [HttpPut("{taskId:guid}/title")]
    public async Task<IActionResult> UpdateTitleAsync(
        string projectId,
        Guid taskId,
        [FromBody] SessionRecordTitleUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var normalizedProjectId = NormalizeProjectId(projectId);

        var existing = await _taskService.GetAsync(taskId);
        if (existing == null || existing.ProjectId != normalizedProjectId)
        {
            return NotFound();
        }

        var updated = await _taskService.UpdateTitleAsync(taskId, request.Title, user);
        return updated == null ? BadRequest("title is required.") : NoContent();
    }

    [HttpDelete("{taskId:guid}/session")]
    public async Task<IActionResult> DeleteSessionAsync(string projectId, Guid taskId)
    {
        var normalizedProjectId = NormalizeProjectId(projectId);

        var existing = await _taskService.GetAsync(taskId);
        if (existing == null || existing.ProjectId != normalizedProjectId)
        {
            return NotFound();
        }

        var deleted = await _taskRecordService.DeleteBySessionIdAsync(existing.ContextId, normalizedProjectId);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{taskId:guid}/reorder")]
    public async Task<IActionResult> ReorderAsync(string projectId, Guid taskId, [FromBody] ProjectTaskReorderRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var normalizedProjectId = NormalizeProjectId(projectId);

        var existing = await _taskService.GetAsync(taskId);
        if (existing == null || existing.ProjectId != normalizedProjectId)
        {
            return NotFound();
        }

        var updated = await _taskService.ReorderAsync(taskId, request.UpdateTimeUtc, user);
        if (updated == null)
        {
            return BadRequest("Only pending tasks can be reordered.");
        }

        var records = await _taskRecordService.GetByContextIdAsync(updated.ContextId);
        return Ok(ToResponse(updated, records, null));
    }

    [HttpPost("{taskId:guid}/cancel")]
    public async Task<IActionResult> CancelAsync(string projectId, Guid taskId)
    {
        var user = User?.Identity?.Name ?? "system";
        var normalizedProjectId = NormalizeProjectId(projectId);

        var existing = await _taskService.GetAsync(taskId);
        if (existing == null || existing.ProjectId != normalizedProjectId)
        {
            return NotFound();
        }

        var canceled = await _taskService.CancelAsync(taskId, user);
        if (canceled == null)
        {
            return BadRequest("Task cannot be canceled in its current state.");
        }

        var records = await _taskRecordService.GetByContextIdAsync(canceled.ContextId);
        return Ok(ToResponse(canceled, records, null));
    }

    [HttpDelete("{taskId:guid}")]
    public async Task<IActionResult> DeleteAsync(string projectId, Guid taskId)
    {
        var user = User?.Identity?.Name ?? "system";
        var normalizedProjectId = NormalizeProjectId(projectId);

        var existing = await _taskService.GetAsync(taskId);
        if (existing == null || existing.ProjectId != normalizedProjectId)
        {
            return NotFound();
        }

        await _taskService.DeleteAsync(taskId, user);
        return Ok();
    }

    private static ProjectTaskResponse ToResponse(
        ProjectTask task,
        IReadOnlyList<TaskRecord> records,
        IReadOnlyList<AgwMessage>? messages)
    {
        var latestRecord = records.LastOrDefault();
        var latestUserRecord = records
            .LastOrDefault(record => record.ToChatMessage()?.Role == ChatRole.User);
        var responseAgentId = task.AgentType == ProjectTaskAgentType.Agent
            ? task.AgentId
            : null;
        var responseAgentflowId = task.AgentType == ProjectTaskAgentType.Agentflow
            ? task.AgentId
            : null;

        return new ProjectTaskResponse(
            task.Id,
            task.ProjectId,
            task.ContextId,
            task.AgentType,
            responseAgentflowId,
            responseAgentId,
            task.Status,
            latestRecord?.SessionId ?? task.ContextId,
            task.Title,
            task.Description,
            GetInputText(latestUserRecord),
            task.ErrorMessage ?? latestRecord?.Error,
            task.CreateTime,
            task.UpdateTime,
            task.Status == ProjectTaskStatus.Pending ? null : task.CreateTime,
            task.FinishedTime,
            CountMessages(records),
            messages);
    }

    private static IEnumerable<AgwMessage> ToAiMessages(TaskRecord record)
    {
        var message = record.ToChatMessage()?.ToAiMessage();
        if (message != null)
        {
            yield return message;
        }
    }

    private static int CountMessages(IEnumerable<TaskRecord> records) =>
        records.Sum(CountMessages);

    private static int CountMessages(TaskRecord record) =>
        record.ToChatMessage() == null ? 0 : 1;

    private static string NormalizeProjectId(string projectId)
    {
        var normalizedProjectId = projectId.Trim();
        return Guid.TryParse(normalizedProjectId, out var parsedProjectId)
            ? parsedProjectId.Normalize()
            : normalizedProjectId;
    }

    private static string GetInputText(TaskRecord? record)
    {
        if (record?.ToChatMessage()?.Role != ChatRole.User)
        {
            return string.Empty;
        }

        return record.GetText();
    }
}
