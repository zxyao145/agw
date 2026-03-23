using Agw.Shared.Abstractions.Repositories;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Services;

public class TaskAppService : ITaskAppService
{
    private readonly IRepository<ProjectTask> _taskRepository;
    private readonly IRepository<TaskRecord> _recordRepository;
    private readonly ProjectResolver _projectResolver;

    public TaskAppService(
        IRepository<ProjectTask> taskRepository,
        IRepository<TaskRecord> recordRepository,
        ProjectResolver projectResolver)
    {
        _taskRepository = taskRepository;
        _recordRepository = recordRepository;
        _projectResolver = projectResolver;
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

        var exist = await _taskRepository.Queryable.AnyAsync(r => r.Id == taskGuid, cancellationToken);
        return exist;
    }

    public async Task<bool> HasSessionAsync(
        string sessionId,
        Guid? projectId = null,
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

        if (projectId == null)
        {
            return false;
        }

        var project = await _projectResolver.ResolveAsync(projectId, cancellationToken);
        if (project == null)
        {
            return false;
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

        var tasks = await _taskRepository.ListAsync(t => t.ProjectId == project.Id);
        var knownContexts = tasks
            .Select(t => t.ContextId)
            .ToHashSet(StringComparer.Ordinal);

        return contexts.Any(knownContexts.Contains);
    }
}
