using DSystem.Domain.Repositories;
using DSystem.Shared.Tasks;
using DSystem.Shared.Tasks.Entities;
using Microsoft.EntityFrameworkCore;

namespace DSystem.Appliaction.Services;

public class TaskAppService : ITaskAppService
{
    private readonly IRepository<ProjectTask> _taskRepository;
    private readonly IRepository<TaskRecord> _recordRepository;

    public TaskAppService(
        IRepository<ProjectTask> taskRepository,
        IRepository<TaskRecord> recordRepository)
    {
        _taskRepository = taskRepository;
        _recordRepository = recordRepository;
    }

    public Task<ProjectTask?> GetTaskAsync(Guid id) => _taskRepository.GetByIdAsync(id);

    public async Task<bool> HasTaskAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return false;
        }

        if (!Guid.TryParse(taskId, out var taskGuid))
        {
            return false;
        }

        var exist = await _taskRepository.Queryable.AnyAsync(r => r.Id == taskGuid);
        return exist;
    }

    public async Task<bool> HasSessionAsync(
        string sessionId,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var records = await _recordRepository.ListAsync(r => r.SessionId == sessionId);
        if (records.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return true;
        }

        var contexts = records
            .Select(r => r.ContextId)
            .Where(contextId => !string.IsNullOrWhiteSpace(contextId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (contexts.Length == 0)
        {
            return false;
        }

        var tasks = await _taskRepository.ListAsync(t => t.ProjectId == projectId);
        var knownContexts = tasks
            .Select(t => t.ContextId)
            .ToHashSet(StringComparer.Ordinal);

        return contexts.Any(knownContexts.Contains);
    }
}
