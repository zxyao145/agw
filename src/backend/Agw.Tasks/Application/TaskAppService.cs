using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Results;
using Agw.Tasks.Domain.Services;

using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Application;

public class TaskAppService : ITaskAppService
{
    private readonly IRepository<ProjectContext> _contextRepository;
    private readonly IRepository<TaskRecord> _recordRepository;
    private readonly ProjectResolver _projectResolver;
    private readonly TaskExecutionAppService _taskExecutionAppService;

    public TaskAppService(
        IRepository<ProjectContext> contextRepository,
        IRepository<TaskRecord> recordRepository,
        ProjectResolver projectResolver,
        TaskExecutionAppService taskExecutionAppService)
    {
        _contextRepository = contextRepository;
        _recordRepository = recordRepository;
        _projectResolver = projectResolver;
        _taskExecutionAppService = taskExecutionAppService;
    }

    public Task<TaskProjection?> GetTaskAsync(Guid id) => _taskExecutionAppService.GetTaskAsync(id);

    public async Task<TaskProjection?> CreateTaskForExecutionAsync(
        Guid projectId,
        Guid? taskId,
        string input,
        string user,
        string? contextId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedInput = input.Trim();
        var request = new TaskCreateRequest(
            JobId: null,
            Input: normalizedInput,
            Title: TaskTitleFactory.Create(normalizedInput),
            ContextId: contextId);

        var result = await _taskExecutionAppService.CreateForExecutionAsync(projectId, taskId, request, user);
        if (result.Value == null)
        {
            return null;
        }

        return await _taskExecutionAppService.GetTaskAsync(result.Value.TaskId);
    }

    public async Task<bool> HasTaskAsync(
        Guid taskId,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        if (projectId != null)
        {
            var project = await _projectResolver.ResolveAsync(projectId, cancellationToken);
            if (project == null)
            {
                return false;
            }
            var existInProject = await _recordRepository.Queryable
                .AnyAsync(
                r => r.TaskId == taskId && r.ProjectContext != null && r.ProjectContext.ProjectId == project.Id,
                cancellationToken
                );
            return existInProject;
        }

        var exist = await _recordRepository.Queryable.AnyAsync(r => r.TaskId == taskId, cancellationToken);
        return exist;
    }



    #region ResolveTaskAsync

    public async Task<ExecutionTaskResolutionResult> ResolveTaskAsync(
   ExecutionTaskRequest request,
   CancellationToken cancellationToken)
    {
        var resolvedProjectId = await _projectResolver.ResolveProjectIdAsync(request.ProjectId);
        if (!resolvedProjectId.HasValue)
        {
            return new ExecutionTaskResolutionResult(null, AgwApiResult.BadRequest("Project not found."));
        }

        if (request.Resume)
        {
            if (!request.TaskId.HasValue || request.TaskId.Value == Guid.Empty)
            {
                return new ExecutionTaskResolutionResult(null, AgwApiResult.BadRequest("TaskId is required when resume is true."));
            }

            var existingTask = await GetTaskAsync(request.TaskId.Value);
            if (existingTask == null)
            {
                return new ExecutionTaskResolutionResult(null, AgwApiResult.BadRequest("Task not found."));
            }

            if (existingTask.ProjectId != resolvedProjectId.Value)
            {
                return new ExecutionTaskResolutionResult(null, AgwApiResult.BadRequest("Task does not belong to the supplied projectId."));
            }

            return new ExecutionTaskResolutionResult(existingTask, null);
        }

        if (!request.TaskId.HasValue || request.TaskId.Value == Guid.Empty)
        {
            return await CreateTaskAsync(
                resolvedProjectId.Value,
                null,
                request.ContextId,
                request.Input,
                request.User,
                cancellationToken);
        }

        var task = await GetTaskAsync(request.TaskId.Value);
        if (task == null)
        {
            return await CreateTaskAsync(
                resolvedProjectId.Value,
                request.TaskId,
                request.ContextId,
                request.Input,
                request.User,
                cancellationToken);
        }

        if (task.ProjectId != resolvedProjectId.Value)
        {
            return new ExecutionTaskResolutionResult(null, AgwApiResult.BadRequest("Task does not belong to the supplied projectId."));
        }

        return new ExecutionTaskResolutionResult(task, null);
    }


    private async Task<ExecutionTaskResolutionResult> CreateTaskAsync(
        Guid projectId,
        Guid? taskId,
        string? contextId,
        string input,
        string user,
        CancellationToken cancellationToken)
    {
        var task = await CreateTaskForExecutionAsync(
            projectId,
            taskId,
            input,
            user,
            contextId,
            cancellationToken);
        if (task == null)
        {
            return new ExecutionTaskResolutionResult(null, AgwApiResult.BadRequest("Failed to create task."));
        }

        return new ExecutionTaskResolutionResult(task, null);
    }

    #endregion
}
