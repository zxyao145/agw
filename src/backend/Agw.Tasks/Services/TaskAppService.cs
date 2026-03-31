using Agw.Shared.Abstractions.Repositories;
using Agw.Shared.Contracts;
using Agw.Shared.Enums;
using Agw.Shared;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Services;

public class TaskAppService : ITaskAppService
{
    private readonly IRepository<ProjectTask> _taskRepository;
    private readonly IRepository<TaskRecord> _recordRepository;
    private readonly ProjectResolver _projectResolver;
    private readonly ProjectTaskAppService _projectTaskAppService;

    public TaskAppService(
        IRepository<ProjectTask> taskRepository,
        IRepository<TaskRecord> recordRepository,
        ProjectResolver projectResolver,
        ProjectTaskAppService projectTaskAppService)
    {
        _taskRepository = taskRepository;
        _recordRepository = recordRepository;
        _projectResolver = projectResolver;
        _projectTaskAppService = projectTaskAppService;
    }

    public Task<ProjectTask?> GetTaskAsync(Guid id) => _taskRepository.GetByIdAsync(id);

    public async Task<ProjectTask?> CreateTaskForExecutionAsync(
        Guid projectId,
        Guid? taskId,
        AgentRuntimeType agentType,
        Guid executionId,
        string input,
        string? sessionId,
        string user,
        CancellationToken cancellationToken = default)
    {
        var normalizedInput = input.Trim();
        var title = normalizedInput[..Math.Min(normalizedInput.Length, 80)];
        var request = new ProjectTaskCreateRequest(
            agentType,
            agentType == AgentRuntimeType.Agentflow ? executionId : null,
            agentType == AgentRuntimeType.Agent ? executionId : null,
            normalizedInput,
            normalizedInput,
            sessionId,
            title);

        var result = await _projectTaskAppService.CreateForExecutionAsync(projectId, taskId, request, user);
        if (result.Value == null)
        {
            return null;
        }

        return await _taskRepository.GetByIdAsync(result.Value.Id);
    }

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

        var tasks = await _taskRepository.ListAsync(t => t.ProjectId == project.Id);
        var knownTaskIds = tasks
            .Select(t => t.Id.Normalize())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return records.Any(record => knownTaskIds.Contains(record.SessionId));
    }
}
