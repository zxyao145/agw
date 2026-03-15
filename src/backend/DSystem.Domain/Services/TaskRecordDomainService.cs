using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;
using DSystem.Shared;
using System.Linq.Expressions;

namespace DSystem.Domain.Services;

public class TaskRecordDomainService
{
    private readonly IRepository<TaskRecord> _recordRepository;
    private readonly IRepository<ProjectTask> _taskRepository;
    private readonly IUnitOfWork _unitOfWork;

    public TaskRecordDomainService(
        IRepository<TaskRecord> recordRepository,
        IRepository<ProjectTask> taskRepository,
        IUnitOfWork unitOfWork)
    {
        _recordRepository = recordRepository;
        _taskRepository = taskRepository;
        _unitOfWork = unitOfWork;
    }

    public Task<IReadOnlyList<TaskRecord>> ListAsync(Expression<Func<TaskRecord, bool>>? predicate = null) =>
        _recordRepository.ListAsync(predicate);

    public async Task<IReadOnlyList<TaskRecord>> GetByContextIdAsync(string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            return [];
        }

        var records = await _recordRepository.ListAsync(r => r.ContextId == contextId);
        return records
            .OrderBy(r => r.ConversationSequence ?? long.MinValue)
            .ThenBy(r => r.CreateTime)
            .ThenBy(r => r.UpdateTime ?? r.CreateTime)
            .ToList();
    }

    public async Task<IReadOnlyList<TaskRecord>> GetByContextIdsAsync(IEnumerable<string> contextIds)
    {
        var values = contextIds
            .Where(contextId => !string.IsNullOrWhiteSpace(contextId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (values.Length == 0)
        {
            return [];
        }

        var records = await _recordRepository.ListAsync(r => values.Contains(r.ContextId));
        return records
            .OrderBy(r => r.ConversationSequence ?? long.MinValue)
            .ThenBy(r => r.CreateTime)
            .ThenBy(r => r.UpdateTime ?? r.CreateTime)
            .ToList();
    }

    public async Task<TaskRecord?> GetLatestByContextIdAsync(string contextId)
    {
        var records = await GetByContextIdAsync(contextId);
        return records.LastOrDefault();
    }

    public async Task<Dictionary<string, TaskRecord>> GetLatestByContextIdsAsync(IEnumerable<string> contextIds)
    {
        var records = await GetByContextIdsAsync(contextIds);
        return records
            .GroupBy(record => record.ContextId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(record => record.UpdateTime ?? record.CreateTime)
                    .ThenBy(record => record.CreateTime)
                    .Last(),
                StringComparer.Ordinal);
    }

    public async Task<ProjectTask?> FindTaskAsync(string sessionId, string projectId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        var tasks = await _taskRepository.ListAsync(t => t.ProjectId == projectId);
        var directTask = tasks.FirstOrDefault(t =>
            t.ContextId == sessionId
            || string.Equals(t.Id.Normalize(), sessionId, StringComparison.OrdinalIgnoreCase));

        if (directTask != null)
        {
            return directTask;
        }

        if (tasks.Count == 0)
        {
            return null;
        }

        var taskByContext = tasks.ToDictionary(t => t.ContextId, StringComparer.Ordinal);
        var records = await _recordRepository.ListAsync(r => r.SessionId == sessionId);
        var latestMatch = records
            .Where(r => taskByContext.ContainsKey(r.ContextId))
            .OrderByDescending(r => r.UpdateTime ?? r.CreateTime)
            .ThenByDescending(r => r.CreateTime)
            .FirstOrDefault();

        return latestMatch == null
            ? null
            : taskByContext.GetValueOrDefault(latestMatch.ContextId);
    }

    public async Task<IReadOnlyList<TaskRecord>> GetTaskRecordsAsync(string sessionId, string projectId)
    {
        var task = await FindTaskAsync(sessionId, projectId);
        if (task == null)
        {
            return [];
        }

        return await GetByContextIdAsync(task.ContextId);
    }

    public async Task<ProjectTask?> UpdateTaskTitleAsync(
        string sessionId,
        string projectId,
        string title,
        string user)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var task = await FindTaskAsync(sessionId, projectId);
        if (task == null)
        {
            return null;
        }

        task.Title = title.Trim();
        task.UpdateBy = user;
        task.UpdateTime = DateTime.UtcNow;

        _taskRepository.Update(task);
        await _unitOfWork.SaveChangesAsync();
        return task;
    }

    public async Task<bool> DeleteBySessionIdAsync(string sessionId, string projectId)
    {
        var task = await FindTaskAsync(sessionId, projectId);
        if (task == null)
        {
            return false;
        }

        var records = await GetByContextIdAsync(task.ContextId);
        foreach (var record in records)
        {
            _recordRepository.Remove(record);
        }

        if (ShouldDeleteTask(task))
        {
            _taskRepository.Remove(task);
        }

        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    private static bool ShouldDeleteTask(ProjectTask task) =>
        !Guid.TryParse(task.ProjectId, out _);
}
