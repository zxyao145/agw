using Agw.Auth.Contracts;
using Agw.Projects.Domain.Services;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Bens.Results;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public class TaskAppService : ITaskAppService
{
    private readonly IRepository<ProjectConversation> _contextRepository;
    private readonly IRepository<ProjectConversationChatHistory> _recordRepository;
    private readonly ProjectResolver _projectResolver;
    private readonly TaskExecutionAppService _taskExecutionAppService;
    private readonly IUserInfoService _userInfoService;

    public TaskAppService(
        IRepository<ProjectConversation> contextRepository,
        IRepository<ProjectConversationChatHistory> recordRepository,
        ProjectResolver projectResolver,
        TaskExecutionAppService taskExecutionAppService,
        IUserInfoService userInfoService
    )
    {
        _contextRepository = contextRepository;
        _recordRepository = recordRepository;
        _projectResolver = projectResolver;
        _taskExecutionAppService = taskExecutionAppService;
        _userInfoService = userInfoService;
    }

    public Task<TaskProjection?> GetTaskAsync(Guid id) => GetTaskAsync(id, ResolveOwnerUserId());

    public Task<TaskProjection?> GetTaskAsync(Guid id, string? ownerUserId) =>
        _taskExecutionAppService.GetTaskAsync(id, ownerUserId);

    public async Task<TaskProjection?> CreateTaskForExecutionAsync(
        Guid projectId,
        Guid? taskId,
        string input,
        string user,
        string? contextId = null,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedInput = input.Trim();
        var request = new TaskCreateRequest(
            JobId: null,
            Input: normalizedInput,
            Title: TaskTitleFactory.Create(normalizedInput),
            ContextId: contextId
        );

        var result = await _taskExecutionAppService.CreateForExecutionAsync(projectId, taskId, request, user);
        if (result.Value == null)
        {
            return null;
        }

        return await _taskExecutionAppService.GetTaskAsync(result.Value.TaskId, user);
    }

    public async Task<bool> HasTaskAsync(
        Guid taskId,
        Guid? projectId = null,
        CancellationToken cancellationToken = default
    )
    {
        if (projectId != null)
        {
            var project = await _projectResolver.ResolveAsync(projectId, cancellationToken);
            if (project == null)
            {
                return false;
            }
            var existInProject = await _recordRepository.Queryable.AnyAsync(
                r =>
                    r.TaskId == taskId
                    && r.ProjectConversation != null
                    && r.ProjectConversation.ProjectId == project.Id
                    && r.ProjectConversation.CreateBy == project.CreateBy,
                cancellationToken
            );
            return existInProject;
        }

        var ownerUserId = ResolveOwnerUserId();
        var exist = await _taskExecutionAppService.GetTaskAsync(taskId, ownerUserId).ConfigureAwait(false) != null;
        return exist;
    }

    #region ResolveTaskAsync

    public async Task<ExecutionTaskResolutionResult> ResolveTaskAsync(
        ExecutionTaskRequest request,
        CancellationToken cancellationToken
    )
    {
        var ownerUserId = string.IsNullOrWhiteSpace(request.User) ? ResolveOwnerUserId() : request.User.Trim();
        var resolvedProjectId = await _projectResolver
            .ResolveProjectIdForUserAsync(request.ProjectId, ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        if (!resolvedProjectId.HasValue)
        {
            return new ExecutionTaskResolutionResult(
                null,
                ApiResult.BadRequest("Project not found.", ErrorCodes.InvalidParam.Code)
            );
        }

        if (request.Resume)
        {
            if (request.TaskId.HasValue && request.TaskId.Value != Guid.Empty)
            {
                var existingTask = await GetTaskAsync(request.TaskId.Value, ownerUserId);
                if (existingTask == null)
                {
                    return new ExecutionTaskResolutionResult(
                        null,
                        ApiResult.BadRequest("Task not found.", ErrorCodes.InvalidParam.Code)
                    );
                }

                if (existingTask.ProjectId != resolvedProjectId.Value)
                {
                    return new ExecutionTaskResolutionResult(
                        null,
                        ApiResult.BadRequest(
                            "Task does not belong to the supplied projectId.",
                            ErrorCodes.InvalidParam.Code
                        )
                    );
                }

                return new ExecutionTaskResolutionResult(existingTask, null);
            }

            if (string.IsNullOrWhiteSpace(request.ContextId))
            {
                return new ExecutionTaskResolutionResult(
                    null,
                    ApiResult.BadRequest("ContextId is required when resume is true.", ErrorCodes.InvalidParam.Code)
                );
            }

            var latestTask = await GetLatestTaskByContextAsync(
                resolvedProjectId.Value,
                request.ContextId,
                ownerUserId,
                cancellationToken
            );
            if (latestTask == null)
            {
                return new ExecutionTaskResolutionResult(
                    null,
                    ApiResult.BadRequest("ProjectContext not found.", ErrorCodes.InvalidParam.Code)
                );
            }
            return new ExecutionTaskResolutionResult(latestTask, null);
        }

        if (!request.TaskId.HasValue || request.TaskId.Value == Guid.Empty)
        {
            return await CreateTaskAsync(
                resolvedProjectId.Value,
                null,
                request.ContextId,
                request.Input,
                ownerUserId,
                cancellationToken
            );
        }

        var task = await GetTaskAsync(request.TaskId.Value, ownerUserId);
        if (task == null)
        {
            return await CreateTaskAsync(
                resolvedProjectId.Value,
                request.TaskId,
                request.ContextId,
                request.Input,
                ownerUserId,
                cancellationToken
            );
        }

        if (task.ProjectId != resolvedProjectId.Value)
        {
            return new ExecutionTaskResolutionResult(
                null,
                ApiResult.BadRequest("Task does not belong to the supplied projectId.", ErrorCodes.InvalidParam.Code)
            );
        }

        return new ExecutionTaskResolutionResult(task, null);
    }

    /// <summary>
    /// 根据项目和规范化 context ID 查找该上下文中最新的任务投影。
    /// </summary>
    private async Task<TaskProjection?> GetLatestTaskByContextAsync(
        Guid projectId,
        string contextId,
        string? ownerUserId,
        CancellationToken cancellationToken
    )
    {
        var normalizedContextId = ContextIdUtil.NormalizeContextId(contextId);
        var context = await _contextRepository.SingleOrDefaultAsync(item =>
            item.ProjectId == projectId && item.ContextId == normalizedContextId && item.CreateBy == ownerUserId
        );
        if (context == null)
        {
            return null;
        }

        var records = await _recordRepository.ListAsync(record => record.ConversationId == context.Id);
        if (records.Count == 0)
        {
            return null;
        }

        return records
            .GroupBy(record => record.TaskId)
            .Select(group => TaskExecutionMapper.ToTask(context, group.ToList()))
            .OrderByDescending(task => task.UpdateTime ?? task.CreateTime)
            .ThenByDescending(task => task.CreateTime)
            .ThenByDescending(task => task.TaskId)
            .FirstOrDefault();
    }

    private async Task<ExecutionTaskResolutionResult> CreateTaskAsync(
        Guid projectId,
        Guid? taskId,
        string? contextId,
        string input,
        string user,
        CancellationToken cancellationToken
    )
    {
        var task = await CreateTaskForExecutionAsync(projectId, taskId, input, user, contextId, cancellationToken);
        if (task == null)
        {
            return new ExecutionTaskResolutionResult(
                null,
                ApiResult.BadRequest("Failed to create task.", ErrorCodes.InvalidParam.Code)
            );
        }

        return new ExecutionTaskResolutionResult(task, null);
    }

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;

    #endregion
}
