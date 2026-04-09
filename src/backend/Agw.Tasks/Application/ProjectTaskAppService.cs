using System.Linq.Expressions;

using Agw.Shared;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Enums;
using Agw.Shared.Models;
using Agw.Shared.Utils;
using Agw.Tasks.Domain.Services;

using Microsoft.Extensions.AI;

namespace Agw.Tasks.Application;

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

    public async Task<IReadOnlyList<ProjectTaskSummaryResponse>> ListResponsesAsync(Guid projectId)
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

        return tasks
            .OrderByDescending(task => task.UpdateTime ?? task.CreateTime)
            .Select(ToSummaryResponse)
            .ToList();
    }

    public async Task<ProjectTaskResponse?> GetResponseAsync(Guid projectId, Guid taskId)
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

        var records = await GetOrderedRecordsByTaskIdAsync(task.Id);
        var messages = records.SelectMany(ToAiMessages).ToList();
        return ToResponse(task, records, messages);
    }

    public async Task<ApplicationResult<ProjectTaskResponse>> CreateAsync(
        Guid projectId,
        ProjectTaskCreateRequest request,
        string user)
    {
        return await CreateAsync(projectId, request, user, ProjectTaskStatus.Pending);
    }

    public async Task<ApplicationResult<ProjectTaskResponse>> CreateRunningAsync(
        Guid projectId,
        ProjectTaskCreateRequest request,
        string user)
    {
        return await CreateAsync(projectId, request, user, ProjectTaskStatus.Running);
    }

    public async Task<ApplicationResult<ProjectTaskResponse>> CreateForExecutionAsync(
        Guid projectId,
        Guid? taskId,
        ProjectTaskCreateRequest request,
        string user)
    {
        return await CreateAsync(projectId, request, user, ProjectTaskStatus.Pending, taskId);
    }

    private async Task<ApplicationResult<ProjectTaskResponse>> CreateAsync(
        Guid projectId,
        ProjectTaskCreateRequest request,
        string user,
        ProjectTaskStatus initialStatus,
        Guid? taskIdOverride = null)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult<ProjectTaskResponse>.Invalid(
                "Failed to create task (project/target invalid, target mismatch, or input missing).");
        }

        var taskId = taskIdOverride.HasValue && taskIdOverride.Value != Guid.Empty
            ? taskIdOverride.Value
            : Guid.NewGuid();
        var contextId = string.IsNullOrWhiteSpace(request.ContextId)
            ? taskId.Normalize()
            : request.ContextId.Trim();

        var task = new ProjectTask
        {
            Id = taskId,
            ProjectId = project.Id,
            ContextId = contextId,
            JobId = request.JobId,
            Title = request.Title ?? string.Empty,
        };

        var inputMessage = new ChatMessage(ChatRole.User, request.Input.Trim())
        {
            MessageId = Guid.NewGuid().ToString(),
            AuthorName = Constants.DefaultAuthor
        };

        var initialRecord = new TaskRecord
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            ConversationSequence = 0,
            ConversationPayload = JsonUtil.Serialize(inputMessage)
        };

        if (!_projectTaskDomainService.TryPrepareForCreate(task, initialRecord, user, initialStatus))
        {
            return ApplicationResult<ProjectTaskResponse>.Invalid(
                "Failed to create task (project/target invalid, target mismatch, or input missing).");
        }

        await _taskRepository.AddAsync(task);
        await _recordRepository.AddAsync(initialRecord);
        await _unitOfWork.SaveChangesAsync();

        return ApplicationResult<ProjectTaskResponse>.Success(ToResponse(task, [initialRecord], null));
    }

    public async Task<ApplicationResult> UpdateTitleAsync(
        Guid projectId,
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

    public async Task<ApplicationResult> DeleteAsync(Guid projectId, Guid taskId)
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

        var records = await GetOrderedRecordsByTaskIdAsync(task.Id);
        foreach (var record in records)
        {
            _recordRepository.Remove(record);
        }

        _taskRepository.Remove(task);
        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<TaskRecord?> GetLatestRecordAsync(Guid taskId)
    {
        var records = await GetOrderedRecordsByTaskIdAsync(taskId);
        return _taskRecordDomainService.GetLatest(records);
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

    private async Task<IReadOnlyList<TaskRecord>> GetOrderedRecordsByTaskIdAsync(Guid taskId)
    {
        var records = await _recordRepository.ListAsync(record => record.TaskId == taskId);
        return _taskRecordDomainService.Order(records);
    }

    private static ProjectTaskSummaryResponse ToSummaryResponse(ProjectTask task) =>
        new(
            task.Id,
            task.ProjectId.Normalize(),
            task.ContextId,
            task.JobId,
            task.Status,
            task.Title,
            task.ErrorMessage,
            task.CreateTime,
            task.UpdateTime,
            task.FinishedTime,
            GetStartedTime(task));

    private static ProjectTaskResponse ToResponse(
        ProjectTask task,
        IReadOnlyList<TaskRecord> records,
        IReadOnlyList<AgwMessage>? messages)
    {
        var summary = ToSummaryResponse(task);

        return new ProjectTaskResponse(
            summary.Id,
            summary.ProjectId,
            summary.ContextId,
            summary.JobId,
            summary.Status,
            summary.Title,
            GetInputText(records.LastOrDefault(record => record.ToChatMessage()?.Role == ChatRole.User)),
            task.ErrorMessage ?? records.LastOrDefault()?.Error,
            summary.CreateTime,
            summary.UpdateTime,
            GetStartedTime(task),
            summary.FinishedTime,
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

    private static DateTime? GetStartedTime(ProjectTask task) =>
        task.Status == ProjectTaskStatus.Pending ? null : task.CreateTime;

    private static string GetInputText(TaskRecord? record)
    {
        if (record?.ToChatMessage()?.Role != ChatRole.User)
        {
            return string.Empty;
        }

        return record.GetText();
    }
}
