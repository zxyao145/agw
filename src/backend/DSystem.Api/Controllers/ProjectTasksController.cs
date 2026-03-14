using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Shared;
using DSystem.Shared.Contracts;
using DSystem.Shared.Enums;
using DSystem.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace DSystem.Api.Controllers;

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
                    taskRecords.LastOrDefault(),
                    CountMessages(taskRecords),
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
        return Ok(ToResponse(task, records.LastOrDefault(), CountMessages(records), messages));
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

        var initialRecord = new TaskRecord
        {
            Id = Guid.NewGuid(),
            ContextId = contextId,
            SessionId = sessionId,
            Input = CreateUserInputMessage(request.Input)
        };

        var created = await _taskService.CreateAsync(task, initialRecord, user);
        if (created == null)
        {
            return BadRequest("Failed to create task (project/target invalid, target mismatch, or input missing).");
        }

        return Accepted(ToResponse(created, initialRecord, CountMessages([initialRecord]), null));
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

        var latestRecord = await _taskRecordService.GetLatestByContextIdAsync(updated.ContextId);
        return Ok(ToResponse(
            updated,
            latestRecord,
            latestRecord == null ? 0 : CountMessages([latestRecord]),
            null));
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

        var latestRecord = await _taskRecordService.GetLatestByContextIdAsync(updated.ContextId);
        return Ok(ToResponse(
            updated,
            latestRecord,
            latestRecord == null ? 0 : CountMessages([latestRecord]),
            null));
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

        var latestRecord = await _taskRecordService.GetLatestByContextIdAsync(canceled.ContextId);
        return Ok(ToResponse(
            canceled,
            latestRecord,
            latestRecord == null ? 0 : CountMessages([latestRecord]),
            null));
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
        TaskRecord? latestRecord,
        int messageCount,
        IReadOnlyList<AiMessage>? messages)
    {
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
            GetInputText(latestRecord?.Input),
            task.ErrorMessage ?? latestRecord?.Error,
            task.CreateTime,
            task.UpdateTime,
            task.Status == ProjectTaskStatus.Pending ? null : task.CreateTime,
            task.FinishedTime,
            messageCount,
            messages);
    }

    private static UserInputMessage CreateUserInputMessage(string input)
    {
        return new UserInputMessage(
            [new AiMessageContent(AiMessageContentType.TextContent, input.Trim())]);
    }

    private static IEnumerable<AiMessage> ToAiMessages(TaskRecord record)
    {
        var inputMessage = ToUserMessage(record);
        if (inputMessage != null)
        {
            yield return inputMessage;
        }

        foreach (var message in record.Messages)
        {
            yield return message;
        }
    }

    private static AiMessage? ToUserMessage(TaskRecord record)
    {
        var contents = record.Input.Contents;
        if (contents.Count == 0 || contents.All(content => string.IsNullOrWhiteSpace(content.Content?.ToString())))
        {
            return null;
        }

        return new AiMessage(
            $"user_{record.Id:N}",
            "user",
            "user",
            contents,
            record.Input.AdditionalProperties);
    }

    private static int CountMessages(IEnumerable<TaskRecord> records) =>
        records.Sum(CountMessages);

    private static int CountMessages(TaskRecord record)
    {
        var inputCount = record.Input.Contents.Any(content => !string.IsNullOrWhiteSpace(content.Content?.ToString()))
            ? 1
            : 0;
        return inputCount + record.Messages.Count;
    }

    private static string NormalizeProjectId(string projectId)
    {
        var normalizedProjectId = projectId.Trim();
        return Guid.TryParse(normalizedProjectId, out var parsedProjectId)
            ? parsedProjectId.Normalize()
            : normalizedProjectId;
    }

    private static string GetInputText(UserInputMessage? input)
    {
        if (input == null)
        {
            return string.Empty;
        }

        return string.Concat(input.Contents.Select(content => content.Content?.ToString() ?? string.Empty));
    }
}
