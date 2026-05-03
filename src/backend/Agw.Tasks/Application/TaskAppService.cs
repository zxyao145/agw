using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Results;
using Agw.Tasks.Domain.Services;

using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Application;

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
        string input,
        string user,
        CancellationToken cancellationToken = default)
    {
        var normalizedInput = input.Trim();
        var request = new ProjectTaskCreateRequest(
            JobId: null,
            Input: normalizedInput,
            Title: ProjectTaskTitleFactory.Create(normalizedInput));

        var result = await _projectTaskAppService.CreateForExecutionAsync(projectId, taskId, request, user);
        if (result.Value == null)
        {
            return null;
        }

        return await _taskRepository.GetByIdAsync(result.Value.Id);
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
            var existInProject = await _taskRepository.Queryable
                .AnyAsync(
                r => r.Id == taskId && r.ProjectId == project.Id,
                cancellationToken
                );
            return existInProject;
        }

        var exist = await _taskRepository.Queryable.AnyAsync(r => r.Id == taskId, cancellationToken);
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
        string input,
        string user,
        CancellationToken cancellationToken)
    {
        var task = await CreateTaskForExecutionAsync(
            projectId,
            taskId,
            input,
            user,
            cancellationToken);
        if (task == null)
        {
            return new ExecutionTaskResolutionResult(null, AgwApiResult.BadRequest("Failed to create task."));
        }

        return new ExecutionTaskResolutionResult(task, null);
    }

    #endregion
}
