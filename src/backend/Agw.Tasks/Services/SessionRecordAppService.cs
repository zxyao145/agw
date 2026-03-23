using Agw.Domain.Services;
using Agw.Shared;
using Agw.Shared.Abstractions.Repositories;
using Agw.Shared.Contracts;
using Agw.Shared.Models;
using Agw.Shared.Tasks.Entities;

namespace Agw.Tasks.Services;

public class SessionRecordAppService
{
    private readonly IRepository<ProjectTask> _taskRepository;
    private readonly IRepository<TaskRecord> _recordRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ProjectTaskDomainService _projectTaskDomainService;
    private readonly TaskRecordDomainService _taskRecordDomainService;
    private readonly ProjectResolver _projectResolver;

    public SessionRecordAppService(
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

    public async Task<IReadOnlyList<SessionRecordSummary>> ListAsync(string projectId)
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
            .Where(task => recordsByContext.ContainsKey(task.ContextId))
            .Select(task => CreateSummary(task, recordsByContext[task.ContextId]))
            .OrderByDescending(summary => summary.UpdateTime ?? summary.CreateTime)
            .ToList();
    }

    public async Task<SessionRecordDetails?> GetAsync(string sessionId, string projectId)
    {
        var task = await FindTaskAsync(sessionId, projectId);
        if (task == null)
        {
            return null;
        }

        var records = await GetOrderedRecordsByContextIdAsync(task.ContextId);
        if (records.Count == 0)
        {
            return null;
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

        return new SessionRecordDetails(
            task.Id,
            task.ProjectId.Normalize(),
            task.ContextId,
            NormalizeTitle(task.Title),
            messages,
            task.CreateTime,
            updateTime);
    }

    public async Task<ApplicationResult> UpdateTitleAsync(
        string sessionId,
        string projectId,
        string title,
        string user)
    {
        var task = await FindTaskAsync(sessionId, projectId);
        if (task == null)
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

    public async Task<ApplicationResult> DeleteAsync(string sessionId, string projectId)
    {
        var task = await FindTaskAsync(sessionId, projectId);
        if (task == null)
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

    private async Task<ProjectTask?> FindTaskAsync(string sessionId, string projectId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return null;
        }

        var tasks = await _taskRepository.ListAsync(task => task.ProjectId == project.Id);
        if (tasks.Count == 0)
        {
            return null;
        }

        var directTask = _taskRecordDomainService.FindTask(sessionId, tasks, []);
        if (directTask != null)
        {
            return directTask;
        }

        var records = await _recordRepository.ListAsync(record => record.SessionId == sessionId);
        return _taskRecordDomainService.FindTask(sessionId, tasks, records);
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

    private static IEnumerable<AgwMessage> ToAiMessages(TaskRecord record)
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
            task.ProjectId.Normalize(),
            task.ContextId,
            NormalizeTitle(task.Title),
            messageCount,
            task.CreateTime,
            updateTime);
    }

    private static int CountMessages(TaskRecord record) =>
        record.ToChatMessage() == null ? 0 : 1;
}
