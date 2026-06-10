using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Extensions;
using Agw.Tasks.Domain.Services;

using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Application;

public class ProjectContextAppService
{
    private readonly IRepository<ProjectTask> _taskRepository;
    private readonly IRepository<TaskRecord> _recordRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ProjectResolver _projectResolver;
    private readonly ProjectTaskDomainService _projectTaskDomainService;
    private readonly TaskRecordDomainService _taskRecordDomainService;

    public ProjectContextAppService(
        IRepository<ProjectTask> taskRepository,
        IRepository<TaskRecord> recordRepository,
        IUnitOfWork unitOfWork,
        ProjectResolver projectResolver,
        ProjectTaskDomainService projectTaskDomainService,
        TaskRecordDomainService taskRecordDomainService)
    {
        _taskRepository = taskRepository;
        _recordRepository = recordRepository;
        _unitOfWork = unitOfWork;
        _projectResolver = projectResolver;
        _projectTaskDomainService = projectTaskDomainService;
        _taskRecordDomainService = taskRecordDomainService;
    }

    public async Task<IReadOnlyList<ProjectContextSummaryResponse>> ListResponsesAsync(Guid projectId)
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

        var taskIds = tasks.Select(task => task.Id).ToHashSet();
        var records = await _recordRepository.ListAsync(record => taskIds.Contains(record.TaskId));
        var messageCounts = records
            .GroupBy(record => record.TaskId)
            .ToDictionary(group => group.Key, group => ProjectTaskResponseMapper.CountMessages(group));

        return tasks
            .GroupBy(task => task.ContextId, StringComparer.Ordinal)
            .Select(group => ToSummaryResponse(project.Id, group.ToList(), messageCounts))
            .OrderByDescending(context => context.UpdateTime ?? context.CreateTime)
            .ToList();
    }

    public async Task<ProjectContextResponse?> GetResponseAsync(Guid projectId, string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            return null;
        }

        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return null;
        }

        var normalizedContextId = contextId.Trim();
        var tasks = await _taskRepository.ListAsync(task =>
            task.ProjectId == project.Id && task.ContextId == normalizedContextId);

        return await ToResponseAsync(project.Id, normalizedContextId, tasks);
    }

    public async Task<ProjectContextResponse?> GetResponseByTaskIdAsync(Guid projectId, Guid taskId)
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

        var tasks = await _taskRepository.ListAsync(item =>
            item.ProjectId == project.Id && item.ContextId == task.ContextId);

        return await ToResponseAsync(project.Id, task.ContextId, tasks);
    }

    public async Task<ApplicationResult> ClearRecordsAsync(Guid projectId, string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            return ApplicationResult.NotFound();
        }

        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult.NotFound();
        }

        var normalizedContextId = contextId.Trim();
        var tasks = await _taskRepository.ListAsync(task =>
            task.ProjectId == project.Id && task.ContextId == normalizedContextId);
        if (tasks.Count == 0)
        {
            return ApplicationResult.NotFound();
        }

        var taskIds = tasks.Select(task => task.Id).ToHashSet();
        await _recordRepository.Queryable
            .Where(record => taskIds.Contains(record.TaskId))
            .ExecuteDeleteAsync();

        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<ApplicationResult> UpdateTitleAsync(Guid projectId, string contextId, string title, string user)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            return ApplicationResult.NotFound();
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return ApplicationResult.Invalid("title is required.");
        }

        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult.NotFound();
        }

        var normalizedContextId = contextId.Trim();
        var tasks = await _taskRepository.ListAsync(task =>
            task.ProjectId == project.Id && task.ContextId == normalizedContextId);
        if (tasks.Count == 0)
        {
            return ApplicationResult.NotFound();
        }

        foreach (var task in tasks)
        {
            _projectTaskDomainService.TryUpdateTitle(task, title, user);
            _taskRepository.Update(task);
        }

        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<ApplicationResult> DeleteAllAsync(Guid projectId)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult.NotFound();
        }

        var tasks = await _taskRepository.ListAsync(task => task.ProjectId == project.Id);
        if (tasks.Count == 0)
        {
            return ApplicationResult.Success();
        }

        await DeleteTasksAsync(tasks);
        return ApplicationResult.Success();
    }

    public async Task<bool> DeleteAsync(Guid projectId, string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            return false;
        }

        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return false;
        }

        var normalizedContextId = contextId.Trim();
        var tasks = await _taskRepository.ListAsync(task =>
            task.ProjectId == project.Id && task.ContextId == normalizedContextId);
        if (tasks.Count == 0)
        {
            return false;
        }

        await DeleteTasksAsync(tasks);
        return true;
    }

    private async Task DeleteTasksAsync(IReadOnlyList<ProjectTask> tasks)
    {
        var taskIds = tasks.Select(task => task.Id).ToHashSet();
        await _recordRepository.Queryable
            .Where(record => taskIds.Contains(record.TaskId))
            .ExecuteDeleteAsync();

        foreach (var task in tasks)
        {
            _taskRepository.Remove(task);
        }

        await _unitOfWork.SaveChangesAsync();
    }

    private async Task<ProjectContextResponse?> ToResponseAsync(
        Guid projectId,
        string contextId,
        IReadOnlyList<ProjectTask> tasks)
    {
        if (tasks.Count == 0)
        {
            return null;
        }

        var orderedTasks = OrderTasks(tasks).ToList();
        var taskIds = orderedTasks.Select(task => task.Id).ToHashSet();
        var records = await _recordRepository.ListAsync(record => taskIds.Contains(record.TaskId));
        var recordsByTaskId = records
            .GroupBy(record => record.TaskId)
            .ToDictionary(
                group => group.Key,
                group => _taskRecordDomainService.Order(group));

        var messages = orderedTasks
            .SelectMany(task => recordsByTaskId.GetValueOrDefault(task.Id) ?? [])
            .SelectMany(ProjectTaskResponseMapper.ToAiMessages)
            .ToList();
        var latestTask = GetLatestTask(orderedTasks);

        return new ProjectContextResponse(
            projectId.Normalize(),
            contextId,
            latestTask?.Id,
            orderedTasks.Select(ProjectTaskResponseMapper.ToSummaryResponse).ToList(),
            messages.Count,
            messages);
    }

    private static ProjectContextSummaryResponse ToSummaryResponse(
        Guid projectId,
        IReadOnlyList<ProjectTask> tasks,
        IReadOnlyDictionary<Guid, int> messageCounts)
    {
        var orderedTasks = OrderTasks(tasks).ToList();
        var firstTask = orderedTasks.First();
        var latestTask = GetLatestTask(orderedTasks)!;

        return new ProjectContextSummaryResponse(
            projectId.Normalize(),
            firstTask.ContextId,
            latestTask.Title,
            latestTask.Id,
            latestTask.Status,
            tasks.Count,
            tasks.Sum(task => messageCounts.GetValueOrDefault(task.Id)),
            orderedTasks.Min(task => task.CreateTime),
            orderedTasks.Max(task => task.UpdateTime ?? task.CreateTime),
            latestTask.ErrorMessage);
    }

    private static IEnumerable<ProjectTask> OrderTasks(IEnumerable<ProjectTask> tasks) =>
        tasks
            .OrderBy(task => task.CreateTime)
            .ThenBy(task => task.UpdateTime ?? task.CreateTime)
            .ThenBy(task => task.Id);

    private static ProjectTask? GetLatestTask(IEnumerable<ProjectTask> tasks) =>
        tasks
            .OrderByDescending(task => task.UpdateTime ?? task.CreateTime)
            .ThenByDescending(task => task.CreateTime)
            .ThenByDescending(task => task.Id)
            .FirstOrDefault();
}
