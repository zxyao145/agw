using Agw.Projects.Contracts.Execution;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application.Facades;

public sealed class ProjectTaskFacade : IProjectTaskFacade
{
    private readonly TaskExecutionAppService _taskService;
    private readonly ITaskAppService _taskResolver;
    private readonly IRepository<ProjectConversation> _conversationRepository;
    private readonly IRepository<ProjectConversationChatHistory> _historyRepository;

    public ProjectTaskFacade(
        TaskExecutionAppService taskService,
        IRepository<ProjectConversation> conversationRepository,
        IRepository<ProjectConversationChatHistory> historyRepository,
        ITaskAppService taskResolver
    )
    {
        _taskService = taskService;
        _conversationRepository = conversationRepository;
        _historyRepository = historyRepository;
        _taskResolver = taskResolver;
    }

    public async Task<ProjectTaskSnapshot> ResolveAsync(
        ResolveProjectTaskRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var resolution = await _taskResolver
            .ResolveTaskAsync(
                new ExecutionTaskRequest(
                    request.TaskId,
                    request.ProjectId,
                    request.ContextId,
                    request.Input,
                    request.Resume,
                    request.OwnerUserId
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        return resolution.Task == null
            ? throw new AgwException(ErrorCodes.InvalidParam, "Execution task could not be resolved.")
            : Map(resolution.Task);
    }

    public async Task<ProjectTaskSnapshot?> GetAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = await _taskService.GetTaskAsync(taskId).ConfigureAwait(false);
        return task == null ? null : Map(task);
    }

    public async Task<ProjectTaskSnapshot> GetOrCreateAsync(
        StartProjectTaskRequest request,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var existing = await _taskService.GetTaskAsync(request.TaskId).ConfigureAwait(false);
        if (existing != null)
        {
            if (existing.ProjectId != request.ProjectId)
            {
                throw new AgwException(ErrorCodes.TaskIdMismatch);
            }

            return Map(existing);
        }

        var createRequest = new TaskCreateRequest(request.JobId, request.Input, request.Title, request.ContextId);
        var result =
            request.InitialStatus == ProjectTaskStatus.Running
                ? await _taskService
                    .CreateRunningForExecutionAsync(
                        request.ProjectId,
                        request.TaskId,
                        createRequest,
                        request.OwnerUserId
                    )
                    .ConfigureAwait(false)
                : await _taskService
                    .CreateForExecutionAsync(request.ProjectId, request.TaskId, createRequest, request.OwnerUserId)
                    .ConfigureAwait(false);
        if (result.Type != ApplicationResultType.Success || result.Value == null)
        {
            throw new AgwException(
                ErrorCodes.TaskCreationFailed,
                result.Error ?? "Failed to create the execution task."
            );
        }

        var created = await _taskService.GetTaskAsync(request.TaskId).ConfigureAwait(false);
        return created == null
            ? throw new AgwException(ErrorCodes.TaskCreationFailed, "Failed to reload the execution task.")
            : Map(created);
    }

    public async Task<ProjectTaskSnapshot?> FinishAsync(
        FinishProjectTaskRequest request,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskProjection? task = request.Status switch
        {
            ProjectTaskStatus.Succeeded => await _taskService
                .MarkSucceededAsync(request.TaskId, request.OwnerUserId)
                .ConfigureAwait(false),
            ProjectTaskStatus.Failed => await _taskService
                .MarkFailedAsync(request.TaskId, request.ErrorMessage ?? "The execution failed.", request.OwnerUserId)
                .ConfigureAwait(false),
            _ => throw new AgwException(ErrorCodes.InvalidParam, $"Task outcome '{request.Status}' is not supported."),
        };
        return task == null ? null : Map(task);
    }

    public async Task<IReadOnlyDictionary<Guid, string?>> ResolveContextIdsAsync(
        IReadOnlyCollection<Guid> taskIds,
        CancellationToken cancellationToken = default
    )
    {
        if (taskIds.Count == 0)
        {
            return new Dictionary<Guid, string?>();
        }

        var ids = taskIds.ToHashSet();
        var histories = await _historyRepository
            .Queryable.AsNoTracking()
            .Where(record => ids.Contains(record.TaskId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var conversationIds = histories.Select(record => record.ConversationId).ToHashSet();
        var conversations = await _conversationRepository
            .Queryable.AsNoTracking()
            .Where(conversation => conversationIds.Contains(conversation.Id))
            .ToDictionaryAsync(conversation => conversation.Id, cancellationToken)
            .ConfigureAwait(false);

        return histories
            .GroupBy(record => record.TaskId)
            .ToDictionary(
                group => group.Key,
                group => conversations.GetValueOrDefault(group.First().ConversationId)?.ContextId
            );
    }

    internal static ProjectTaskSnapshot Map(TaskProjection task) =>
        new(
            task.TaskId,
            task.ProjectConversationId,
            task.ProjectId,
            task.ContextId,
            task.JobId,
            task.Title,
            Map(task.Status),
            task.ErrorMessage,
            task.CreateTime,
            task.UpdateTime,
            task.FinishedTime
        );

    internal static ProjectTaskStatus Map(TaskExecutionStatus status) =>
        status switch
        {
            TaskExecutionStatus.Pending => ProjectTaskStatus.Pending,
            TaskExecutionStatus.Running => ProjectTaskStatus.Running,
            TaskExecutionStatus.Succeeded => ProjectTaskStatus.Succeeded,
            TaskExecutionStatus.Failed => ProjectTaskStatus.Failed,
            TaskExecutionStatus.Canceled => ProjectTaskStatus.Canceled,
            _ => throw new AgwException(ErrorCodes.InvalidParam, $"Unsupported task status '{status}'."),
        };

    internal static TaskExecutionStatus Map(ProjectTaskStatus status) =>
        status switch
        {
            ProjectTaskStatus.Pending => TaskExecutionStatus.Pending,
            ProjectTaskStatus.Running => TaskExecutionStatus.Running,
            ProjectTaskStatus.Succeeded => TaskExecutionStatus.Succeeded,
            ProjectTaskStatus.Failed => TaskExecutionStatus.Failed,
            ProjectTaskStatus.Canceled => TaskExecutionStatus.Canceled,
            _ => throw new AgwException(ErrorCodes.InvalidParam, $"Unsupported task status '{status}'."),
        };
}
