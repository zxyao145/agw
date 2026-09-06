using Agw.Auth.Contracts;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Domain.Services;
using Agw.Shared.Exceptions;
using Bens.Results;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public class TaskAppService : ITaskAppService
{
    private readonly IProjectsDbContext _dbContext;
    private readonly ProjectResolver _projectResolver;
    private readonly TaskExecutionAppService _taskExecutionAppService;
    private readonly IUserInfoService _userInfoService;

    public TaskAppService(
        IProjectsDbContext dbContext,
        ProjectResolver projectResolver,
        TaskExecutionAppService taskExecutionAppService,
        IUserInfoService userInfoService
    )
    {
        _dbContext = dbContext;
        _projectResolver = projectResolver;
        _taskExecutionAppService = taskExecutionAppService;
        _userInfoService = userInfoService;
    }

    public Task<TaskProjection?> GetTaskAsync(Guid id) => GetTaskAsync(id, ResolveOwnerUserId());

    public Task<TaskProjection?> GetTaskAsync(Guid id, string? ownerUserId) =>
        _taskExecutionAppService.GetTaskAsync(id, ownerUserId);

    public Task<TaskProjection?> CreateTaskForExecutionAsync(
        Guid projectId,
        Guid? taskId,
        string input,
        string user,
        string? contextId = null,
        CancellationToken cancellationToken = default
    ) =>
        CreateTaskForExecutionCoreAsync(
            projectId,
            conversationId: null,
            taskId,
            input,
            user,
            contextId,
            cancellationToken
        );

    private async Task<TaskProjection?> CreateTaskForExecutionCoreAsync(
        Guid projectId,
        Guid? conversationId,
        Guid? taskId,
        string input,
        string user,
        string? contextId,
        CancellationToken cancellationToken
    )
    {
        var normalizedInput = input.Trim();
        var request = new TaskCreateRequest(
            JobId: null,
            Input: normalizedInput,
            Title: TaskTitleFactory.Create(normalizedInput),
            ContextId: contextId
        );

        var result = await _taskExecutionAppService.CreateForExecutionAsync(
            projectId,
            conversationId,
            taskId,
            request,
            user
        );
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
            var existInProject = await _dbContext.ProjectConversationChatHistories.AnyAsync(
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
        if (request.ConversationId == Guid.Empty)
        {
            return new ExecutionTaskResolutionResult(
                null,
                ApiResult.BadRequest("ConversationId is required.", ErrorCodes.InvalidParam.Code)
            );
        }

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

                if (
                    existingTask.ProjectId != resolvedProjectId.Value
                    || existingTask.ProjectConversationId != request.ConversationId
                    || !MatchesContext(existingTask, request.ContextId)
                )
                {
                    return new ExecutionTaskResolutionResult(
                        null,
                        ApiResult.BadRequest(
                            "Task does not belong to the supplied project conversation.",
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

            var latestTask = await GetLatestTaskByConversationAsync(
                resolvedProjectId.Value,
                request.ConversationId,
                request.ContextId,
                ownerUserId,
                cancellationToken
            );
            if (latestTask == null)
            {
                var resetContext = await _dbContext
                    .ProjectConversations.AsNoTracking()
                    .AnyAsync(
                        conversation =>
                            conversation.Id == request.ConversationId
                            && conversation.ProjectId == resolvedProjectId.Value
                            && conversation.CreateBy == ownerUserId
                            && conversation.ContextId == ContextIdUtil.NormalizeContextId(request.ContextId)
                            && conversation.Generation > 0,
                        cancellationToken
                    );
                if (resetContext)
                {
                    return await CreateTaskAsync(
                        resolvedProjectId.Value,
                        request.ConversationId,
                        null,
                        request.ContextId,
                        request.Input,
                        ownerUserId,
                        cancellationToken
                    );
                }
                return new ExecutionTaskResolutionResult(
                    null,
                    ApiResult.BadRequest("ProjectConversation not found.", ErrorCodes.InvalidParam.Code)
                );
            }
            return new ExecutionTaskResolutionResult(latestTask, null);
        }

        if (!request.TaskId.HasValue || request.TaskId.Value == Guid.Empty)
        {
            return await CreateTaskAsync(
                resolvedProjectId.Value,
                request.ConversationId,
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
                request.ConversationId,
                request.TaskId,
                request.ContextId,
                request.Input,
                ownerUserId,
                cancellationToken
            );
        }

        if (
            task.ProjectId != resolvedProjectId.Value
            || task.ProjectConversationId != request.ConversationId
            || !MatchesContext(task, request.ContextId)
        )
        {
            return new ExecutionTaskResolutionResult(
                null,
                ApiResult.BadRequest(
                    "Task does not belong to the supplied project conversation.",
                    ErrorCodes.InvalidParam.Code
                )
            );
        }

        return new ExecutionTaskResolutionResult(task, null);
    }

    /// <summary>
    /// 根据 Project Conversation ID 查找该会话中最新的任务投影，并校验执行 context。
    /// </summary>
    private async Task<TaskProjection?> GetLatestTaskByConversationAsync(
        Guid projectId,
        Guid conversationId,
        string contextId,
        string? ownerUserId,
        CancellationToken cancellationToken
    )
    {
        var normalizedContextId = ContextIdUtil.NormalizeContextId(contextId);
        var conversation = await _dbContext
            .ProjectConversations.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == conversationId && item.ProjectId == projectId && item.CreateBy == ownerUserId,
                cancellationToken
            );
        if (
            conversation == null
            || !string.Equals(
                ContextIdUtil.NormalizeContextId(conversation.ContextId),
                normalizedContextId,
                StringComparison.Ordinal
            )
        )
        {
            return null;
        }

        var records = await _dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record => record.ConversationId == conversation.Id)
            .ToListAsync(cancellationToken);
        if (records.Count == 0)
        {
            return null;
        }

        return records
            .GroupBy(record => record.TaskId)
            .Select(group => TaskExecutionMapper.ToTask(conversation, group.ToList()))
            .OrderByDescending(task => task.UpdateTime ?? task.CreateTime)
            .ThenByDescending(task => task.CreateTime)
            .ThenByDescending(task => task.TaskId)
            .FirstOrDefault();
    }

    private async Task<ExecutionTaskResolutionResult> CreateTaskAsync(
        Guid projectId,
        Guid conversationId,
        Guid? taskId,
        string? contextId,
        string input,
        string user,
        CancellationToken cancellationToken
    )
    {
        var task = await CreateTaskForExecutionCoreAsync(
            projectId,
            conversationId,
            taskId,
            input,
            user,
            contextId,
            cancellationToken
        );
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

    private static bool MatchesContext(TaskProjection task, string? contextId) =>
        string.IsNullOrWhiteSpace(contextId)
        || string.Equals(task.ContextId, ContextIdUtil.NormalizeContextId(contextId), StringComparison.Ordinal);

    #endregion
}
