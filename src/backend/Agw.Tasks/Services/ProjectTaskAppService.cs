using Agw.Domain.Services;
using Agw.Shared;
using Agw.Shared.Abstractions.Repositories;
using Agw.Shared.Contracts;
using Agw.Shared.Enums;
using Agw.Shared.Models;
using Agw.Shared.Tasks.Entities;
using Agw.Shared.Utils;
using Microsoft.Extensions.AI;
using System.Linq.Expressions;

namespace Agw.Tasks.Services;

public class ProjectTaskAppService
{
    private readonly IRepository<ProjectTask> _taskRepository;
    private readonly IRepository<TaskRecord> _recordRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ProjectTaskDomainService _projectTaskDomainService;
    private readonly TaskRecordDomainService _taskRecordDomainService;
    private readonly ProjectResolver _projectResolver;

    public ProjectTaskAppService(
        IRepository<ProjectTask> taskRepository,
        IRepository<TaskRecord> recordRepository,
        IUnitOfWork unitOfWork,
        ProjectTaskDomainService projectTaskDomainService,
        TaskRecordDomainService taskRecordDomainService,
        ProjectResolver projectResolver)
    {
        _taskRepository = taskRepository;
        _recordRepository = recordRepository;
        _unitOfWork = unitOfWork;
        _projectTaskDomainService = projectTaskDomainService;
        _taskRecordDomainService = taskRecordDomainService;
        _projectResolver = projectResolver;
    }

    public Task<IReadOnlyList<ProjectTask>> ListAsync(Expression<Func<ProjectTask, bool>>? predicate = null) =>
        _taskRepository.ListAsync(predicate);

    public Task<ProjectTask?> GetTaskAsync(Guid id) => _taskRepository.GetByIdAsync(id);

    public async Task<IReadOnlyList<ProjectTaskResponse>> ListResponsesAsync(string projectId)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return [];
        }

        var tasks = await _taskRepository.ListAsync(task => task.ProjectId == project.Id);
        if (tasks.Count == 0)
        {
            return [];
        }

        var recordsByContext = await GetRecordsByContextAsync(tasks.Select(task => task.ContextId));
        return tasks
            .OrderByDescending(task => task.UpdateTime ?? task.CreateTime)
            .Select(task => ToResponse(
                task,
                recordsByContext.GetValueOrDefault(task.ContextId) ?? [],
                null))
            .ToList();
    }

    public async Task<ProjectTaskResponse?> GetResponseAsync(string projectId, Guid taskId)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return null;
        }

        var task = await _taskRepository.GetByIdAsync(taskId);
        if (task == null || task.ProjectId != project.Id)
        {
            return null;
        }

        var records = await GetOrderedRecordsByContextIdAsync(task.ContextId);
        var messages = records.SelectMany(ToAiMessages).ToList();
        return ToResponse(task, records, messages);
    }

    public async Task<ApplicationResult<ProjectTaskResponse>> CreateAsync(
        string projectId,
        ProjectTaskCreateRequest request,
        string user)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult<ProjectTaskResponse>.Invalid(
                "Failed to create task (project/target invalid, target mismatch, or input missing).");
        }

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
            ProjectId = project.Id,
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

        var inputMessage = new ChatMessage(ChatRole.User, request.Input.Trim())
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
            ConversationPayload = JsonUtil.Serialize(inputMessage)
        };

        if (!_projectTaskDomainService.TryPrepareForCreate(task, initialRecord, user))
        {
            return ApplicationResult<ProjectTaskResponse>.Invalid(
                "Failed to create task (project/target invalid, target mismatch, or input missing).");
        }

        await _taskRepository.AddAsync(task);
        await _recordRepository.AddAsync(initialRecord);
        await _unitOfWork.SaveChangesAsync();

        return ApplicationResult<ProjectTaskResponse>.Success(ToResponse(task, [initialRecord], null));
    }

    public async Task<ApplicationResult<ProjectTaskResponse>> UpdateAsync(
        string projectId,
        Guid taskId,
        ProjectTaskUpdateRequest request,
        string user)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult<ProjectTaskResponse>.NotFound();
        }

        var existing = await _taskRepository.GetByIdAsync(taskId);
        if (existing == null || existing.ProjectId != project.Id)
        {
            return ApplicationResult<ProjectTaskResponse>.NotFound();
        }

        if (existing.Status != ProjectTaskStatus.Pending)
        {
            return ApplicationResult<ProjectTaskResponse>.Invalid("Only pending tasks can be updated.");
        }

        var records = await GetOrderedRecordsByContextIdAsync(existing.ContextId);
        var latestRecord = _taskRecordDomainService.GetLatest(records);
        if (latestRecord == null
            || !_projectTaskDomainService.TryApplyUpdate(existing, latestRecord, request.Description, request.Input, out var newRecord)
            || newRecord == null)
        {
            return ApplicationResult<ProjectTaskResponse>.Invalid("Failed to update task.");
        }

        _taskRepository.Update(existing);
        await _recordRepository.AddAsync(newRecord);
        await _unitOfWork.SaveChangesAsync();

        var updatedRecords = _taskRecordDomainService.Order(records.Concat([newRecord]));
        return ApplicationResult<ProjectTaskResponse>.Success(ToResponse(existing, updatedRecords, null));
    }

    public async Task<ApplicationResult> UpdateTitleAsync(
        string projectId,
        Guid taskId,
        string title,
        string user)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult.NotFound();
        }

        var task = await _taskRepository.GetByIdAsync(taskId);
        if (task == null || task.ProjectId != project.Id)
        {
            return ApplicationResult.NotFound();
        }

        if (!_projectTaskDomainService.TryUpdateTitle(task, title, user))
        {
            return ApplicationResult.Invalid("title is required.");
        }

        _taskRepository.Update(task);
        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<ApplicationResult> DeleteSessionAsync(string projectId, Guid taskId)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult.NotFound();
        }

        var task = await _taskRepository.GetByIdAsync(taskId);
        if (task == null || task.ProjectId != project.Id)
        {
            return ApplicationResult.NotFound();
        }

        var records = await GetOrderedRecordsByContextIdAsync(task.ContextId);
        foreach (var record in records)
        {
            _recordRepository.Remove(record);
        }

        if (_taskRecordDomainService.ShouldDeleteTask(task))
        {
            _taskRepository.Remove(task);
        }

        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<ApplicationResult<ProjectTaskResponse>> ReorderAsync(
        string projectId,
        Guid taskId,
        DateTime updateTimeUtc,
        string user)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult<ProjectTaskResponse>.NotFound();
        }

        var task = await _taskRepository.GetByIdAsync(taskId);
        if (task == null || task.ProjectId != project.Id)
        {
            return ApplicationResult<ProjectTaskResponse>.NotFound();
        }

        if (!_projectTaskDomainService.TryReorder(task, updateTimeUtc, user))
        {
            return ApplicationResult<ProjectTaskResponse>.Invalid("Only pending tasks can be reordered.");
        }

        _taskRepository.Update(task);
        await _unitOfWork.SaveChangesAsync();

        var records = await GetOrderedRecordsByContextIdAsync(task.ContextId);
        return ApplicationResult<ProjectTaskResponse>.Success(ToResponse(task, records, null));
    }

    public async Task<ApplicationResult<ProjectTaskResponse>> CancelAsync(
        string projectId,
        Guid taskId,
        string user)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult<ProjectTaskResponse>.NotFound();
        }

        var task = await _taskRepository.GetByIdAsync(taskId);
        if (task == null || task.ProjectId != project.Id)
        {
            return ApplicationResult<ProjectTaskResponse>.NotFound();
        }

        if (!_projectTaskDomainService.TryCancel(task, user))
        {
            return ApplicationResult<ProjectTaskResponse>.Invalid("Task cannot be canceled in its current state.");
        }

        _taskRepository.Update(task);
        await _unitOfWork.SaveChangesAsync();

        var records = await GetOrderedRecordsByContextIdAsync(task.ContextId);
        return ApplicationResult<ProjectTaskResponse>.Success(ToResponse(task, records, null));
    }

    public async Task<ApplicationResult> DeleteAsync(string projectId, Guid taskId)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult.NotFound();
        }

        var task = await _taskRepository.GetByIdAsync(taskId);
        if (task == null || task.ProjectId != project.Id)
        {
            return ApplicationResult.NotFound();
        }

        var records = await GetOrderedRecordsByContextIdAsync(task.ContextId);
        foreach (var record in records)
        {
            _recordRepository.Remove(record);
        }

        _taskRepository.Remove(task);
        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<bool> HasRunningTaskAsync(Guid projectId)
    {
        var running = await _taskRepository.ListAsync(task =>
            task.ProjectId == projectId && task.Status == ProjectTaskStatus.Running);
        return running.Count > 0;
    }

    public async Task<ProjectTask?> GetNextPendingAsync(Guid projectId)
    {
        var pending = await _taskRepository.ListAsync(task =>
            task.ProjectId == projectId && task.Status == ProjectTaskStatus.Pending);
        return _projectTaskDomainService.GetNextPending(pending);
    }

    public async Task<TaskRecord?> GetLatestRecordAsync(string contextId)
    {
        var records = await GetOrderedRecordsByContextIdAsync(contextId);
        return _taskRecordDomainService.GetLatest(records);
    }

    public async Task<ProjectTask?> TryMarkRunningAsync(Guid id, string user)
    {
        var task = await _taskRepository.GetByIdAsync(id);
        if (task == null || !_projectTaskDomainService.TryMarkRunning(task, user))
        {
            return null;
        }

        _taskRepository.Update(task);
        await _unitOfWork.SaveChangesAsync();
        return task;
    }

    public async Task<ProjectTask?> MarkSucceededAsync(Guid id, string user)
    {
        var task = await _taskRepository.GetByIdAsync(id);
        if (task == null || !_projectTaskDomainService.TryMarkSucceeded(task, user))
        {
            return null;
        }

        _taskRepository.Update(task);
        await _unitOfWork.SaveChangesAsync();
        return task;
    }

    public async Task<ProjectTask?> MarkFailedAsync(Guid id, string errorMessage, string user)
    {
        var task = await _taskRepository.GetByIdAsync(id);
        if (task == null || !_projectTaskDomainService.TryMarkFailed(task, errorMessage, user))
        {
            return null;
        }

        _taskRepository.Update(task);
        await _unitOfWork.SaveChangesAsync();
        return task;
    }

    private async Task<IReadOnlyList<TaskRecord>> GetOrderedRecordsByContextIdAsync(string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            return [];
        }

        var records = await _recordRepository.ListAsync(record => record.ContextId == contextId);
        return _taskRecordDomainService.Order(records);
    }

    private async Task<Dictionary<string, List<TaskRecord>>> GetRecordsByContextAsync(IEnumerable<string> contextIds)
    {
        var values = contextIds
            .Where(contextId => !string.IsNullOrWhiteSpace(contextId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (values.Length == 0)
        {
            return [];
        }

        var records = await _recordRepository.ListAsync(record => values.Contains(record.ContextId));
        return _taskRecordDomainService.GroupByContext(records);
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
            task.ProjectId.Normalize(),
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

    private static string GetInputText(TaskRecord? record)
    {
        if (record?.ToChatMessage()?.Role != ChatRole.User)
        {
            return string.Empty;
        }

        return record.GetText();
    }
}
