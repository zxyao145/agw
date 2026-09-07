using System.Linq.Expressions;
using Agw.Auth.Contracts;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Domain.Rules;
using Agw.Projects.Domain.Services;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public class TaskExecutionAppService
{
    private readonly IProjectsDbContext _dbContext;
    private readonly ProjectResolver _projectResolver;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfoService;

    public TaskExecutionAppService(
        IProjectsDbContext dbContext,
        ProjectResolver projectResolver,
        TimeProvider timeProvider,
        IUserInfoService userInfoService
    )
    {
        _dbContext = dbContext;
        _projectResolver = projectResolver;
        _timeProvider = timeProvider;
        _userInfoService = userInfoService;
    }

    public async Task<IReadOnlyList<TaskProjection>> ListAsync(Expression<Func<TaskProjection, bool>>? predicate = null)
    {
        var tasks = await ListProjectedTasksAsync(ResolveOwnerUserId());
        return predicate == null ? tasks : tasks.AsQueryable().Where(predicate).ToList();
    }

    public Task<TaskProjection?> GetTaskAsync(Guid id, string? ownerUserId = null) =>
        GetProjectedTaskAsync(id, ownerUserId);

    public async Task<IReadOnlyList<TaskExecutionSummary>> ListResponsesAsync(Guid projectId)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return [];
        }

        var contexts = await _dbContext
            .ProjectConversations.AsNoTracking()
            .Where(context => context.ProjectId == project.Id && context.CreateBy == project.CreateBy)
            .ToListAsync();
        if (contexts.Count == 0)
        {
            return [];
        }

        var contextIds = contexts.Select(context => context.Id).ToHashSet();
        var records = await _dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record => contextIds.Contains(record.ConversationId))
            .ToListAsync();
        var contextById = contexts.ToDictionary(context => context.Id);

        return records
            .GroupBy(record => record.TaskId)
            .Select(group => TaskExecutionMapper.ToTask(contextById[group.First().ConversationId], group.ToList()))
            .OrderByDescending(task => task.UpdateTime ?? task.CreateTime)
            .Select(TaskExecutionMapper.ToSummary)
            .ToList();
    }

    public async Task<TaskExecutionSnapshot?> GetResponseAsync(Guid projectId, Guid taskId)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return null;
        }

        var records = await GetOrderedRecordsByTaskIdAsync(taskId, project.CreateBy);
        if (records.Count == 0)
        {
            return null;
        }

        var context = await _dbContext
            .ProjectConversations.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == records[0].ConversationId
                && item.ProjectId == project.Id
                && item.CreateBy == project.CreateBy
            );
        if (context == null)
        {
            return null;
        }

        var task = TaskExecutionMapper.ToTask(context, records);
        var messages = records.SelectMany(TaskExecutionMapper.ToAiMessages).ToList();
        return TaskExecutionMapper.ToSnapshot(task, records, messages);
    }

    public Task<ApplicationResult<TaskExecutionSnapshot>> CreateAsync(
        Guid projectId,
        TaskCreateRequest request,
        string user
    )
    {
        return CreateAsync(projectId, request, user, TaskExecutionStatus.Pending);
    }

    public Task<ApplicationResult<TaskExecutionSnapshot>> CreateRunningAsync(
        Guid projectId,
        TaskCreateRequest request,
        string user
    )
    {
        return CreateAsync(projectId, request, user, TaskExecutionStatus.Running);
    }

    public Task<ApplicationResult<TaskExecutionSnapshot>> CreateRunningForExecutionAsync(
        Guid projectId,
        Guid taskId,
        TaskCreateRequest request,
        string user
    )
    {
        return CreateAsync(projectId, request, user, TaskExecutionStatus.Running, taskId);
    }

    public Task<ApplicationResult<TaskExecutionSnapshot>> CreateForExecutionAsync(
        Guid projectId,
        Guid? taskId,
        TaskCreateRequest request,
        string user
    )
    {
        return CreateAsync(projectId, request, user, TaskExecutionStatus.Pending, taskId, conversationId: null);
    }

    internal Task<ApplicationResult<TaskExecutionSnapshot>> CreateForExecutionAsync(
        Guid projectId,
        Guid? conversationId,
        Guid? taskId,
        TaskCreateRequest request,
        string user
    )
    {
        return CreateAsync(projectId, request, user, TaskExecutionStatus.Pending, taskId, conversationId);
    }

    /// <summary>
    /// 创建或复用 Project Conversation 和初始记录，并统一解析请求中的 context ID。
    /// </summary>
    private async Task<ApplicationResult<TaskExecutionSnapshot>> CreateAsync(
        Guid projectId,
        TaskCreateRequest request,
        string user,
        TaskExecutionStatus initialStatus,
        Guid? taskIdOverride = null,
        Guid? conversationId = null
    )
    {
        var project = await _projectResolver.ResolveForUserAsync(projectId, user).ConfigureAwait(false);
        if (project == null)
        {
            return ApplicationResult<TaskExecutionSnapshot>.Invalid(
                "Failed to create task (project/target invalid, target mismatch, or input missing)."
            );
        }

        var now = _timeProvider.GetUtcNow();
        var taskId =
            taskIdOverride.HasValue && taskIdOverride.Value != Guid.Empty
                ? taskIdOverride.Value
                : Guid.CreateVersion7();
        var contextId = ContextIdUtil.ResolveContextId(request.ContextId);
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? TaskTitleFactory.Create(request.Input)
            : request.Title.Trim();

        var (conversation, conversationCreated) = await GetOrCreateConversationAsync(
            project.Id,
            conversationId,
            contextId,
            request.JobId,
            title,
            user,
            now
        );
        var record = new ProjectConversationChatHistory
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversation.Id,
            TaskId = taskId,
            JobId = request.JobId,
            Status = initialStatus,
            CreateTime = now,
            UpdateTime = now,
        };

        await _dbContext.ProjectConversationChatHistories.AddAsync(record);
        try
        {
            await _dbContext.SaveConversationChangesAsync(conversation.Id, conversation.Generation);
        }
        catch (DbUpdateException exception) when (conversationCreated && conversationId.HasValue)
        {
            // SaveChanges is atomic. Detach the failed graph before an owner-scoped requery so an exact concurrent
            // insert can be reused without treating a foreign or mismatched identity as available.
            _dbContext.ProjectConversationChatHistories.Remove(record);
            _dbContext.ProjectConversations.Remove(conversation);
            record.ProjectConversation = null;

            var concurrentConversation = await _dbContext.ProjectConversations.SingleOrDefaultAsync(item =>
                item.Id == conversationId.Value
                && item.ProjectId == project.Id
                && item.ContextId == contextId
                && item.CreateBy == user
            );
            if (concurrentConversation == null)
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "The supplied conversation identity is unavailable.",
                    exception
                );
            }

            conversation = concurrentConversation;
            UpdateExistingConversation(conversation, request.JobId, title, user, now);
            await _dbContext.ProjectConversationChatHistories.AddAsync(record);
            await _dbContext.SaveConversationChangesAsync(conversation.Id, conversation.Generation);
        }

        var task = TaskExecutionMapper.ToTask(conversation, [record]);
        return ApplicationResult<TaskExecutionSnapshot>.Success(TaskExecutionMapper.ToSnapshot(task, [record], null));
    }

    public async Task<ApplicationResult> UpdateTitleAsync(Guid projectId, Guid taskId, string title, string user)
    {
        var context = await GetContextByTaskAsync(projectId, taskId);
        if (context == null)
        {
            return ApplicationResult.NotFound();
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return ApplicationResult.Invalid("title is required.");
        }

        context.Title = title.Trim();
        context.UpdateBy = user;
        context.UpdateTime = _timeProvider.GetUtcNow();
        await _dbContext.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<ApplicationResult> DeleteTaskAsync(Guid projectId, Guid taskId)
    {
        var context = await GetContextByTaskAsync(projectId, taskId);
        if (context == null)
        {
            return ApplicationResult.Success();
        }

        var ownerUserId = ResolveOwnerUserId();
        var records = await GetRecordsByTaskIdAsync(taskId, ownerUserId);
        var conversationIds = records
            .Where(record => record.ConversationId == context.Id)
            .Select(record => record.ConversationId)
            .Distinct()
            .ToArray();
        if (conversationIds.Length > 0)
        {
            await _dbContext
                .ProjectConversationChatHistories.Where(x =>
                    x.TaskId == taskId && conversationIds.Contains(x.ConversationId)
                )
                .ExecuteDeleteAsync();
        }

        await _dbContext.SaveChangesAsync();

        return ApplicationResult.Success();
    }

    public async Task<ApplicationResult> ClearRecordsAsync(Guid projectId, Guid taskId)
    {
        var context = await GetContextByTaskAsync(projectId, taskId);
        if (context == null)
        {
            return ApplicationResult.NotFound();
        }

        var ownerUserId = ResolveOwnerUserId();
        var records = await GetRecordsByTaskIdAsync(taskId, ownerUserId);
        var conversationIds = records
            .Where(record => record.ConversationId == context.Id)
            .Select(record => record.ConversationId)
            .Distinct()
            .ToArray();
        if (conversationIds.Length > 0)
        {
            await _dbContext
                .ProjectConversationChatHistories.Where(x =>
                    x.TaskId == taskId && conversationIds.Contains(x.ConversationId)
                )
                .ExecuteDeleteAsync();
        }

        await _dbContext.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<ProjectConversationChatHistory?> GetLatestRecordAsync(Guid taskId)
    {
        var records = await GetOrderedRecordsByTaskIdAsync(taskId, ResolveOwnerUserId());
        return ProjectConversationChatHistoryRules.GetLatest(records);
    }

    public Task<TaskProjection?> MarkSucceededAsync(Guid id, string user) =>
        MarkTaskAsync(id, TaskExecutionStatus.Succeeded, null, user);

    public Task<TaskProjection?> MarkFailedAsync(Guid id, string errorMessage, string user) =>
        MarkTaskAsync(id, TaskExecutionStatus.Failed, errorMessage, user);

    private async Task<TaskProjection?> MarkTaskAsync(
        Guid id,
        TaskExecutionStatus status,
        string? errorMessage,
        string user
    )
    {
        var records = ProjectConversationChatHistoryRules.Order(
            await _dbContext
                .ProjectConversationChatHistories.Where(record =>
                    record.TaskId == id
                    && record.ProjectConversation!.CreateBy == user
                    && record.ProjectConversation.Project!.CreateBy == user
                )
                .ToListAsync()
                .ConfigureAwait(false)
        );
        if (records.Count == 0)
        {
            return null;
        }

        var context = await _dbContext.ProjectConversations.SingleOrDefaultAsync(item =>
            item.Id == records[0].ConversationId && item.CreateBy == user
        );
        if (context == null)
        {
            return null;
        }

        var task = TaskExecutionMapper.ToTask(context, records);
        if (task.Status != TaskExecutionStatus.Running)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var record in records)
        {
            record.Status = status;
            record.TaskErrorMessage = status == TaskExecutionStatus.Succeeded ? null : errorMessage;
            record.FinishedTime = now;
            record.UpdateTime = now;
        }

        await _dbContext.SaveChangesAsync();

        return TaskExecutionMapper.ToTask(context, records);
    }

    /// <summary>
    /// 获取或创建 Project Conversation，并复用及规范化 SQLite 中仅 GUID 大小写不同的 context ID。
    /// </summary>
    private async Task<(ProjectConversation Conversation, bool Created)> GetOrCreateConversationAsync(
        Guid projectId,
        Guid? conversationId,
        string contextId,
        Guid? jobId,
        string title,
        string user,
        DateTimeOffset now
    )
    {
        if (conversationId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "conversationId is required.");
        }

        ProjectConversation? conversation = null;
        if (conversationId.HasValue)
        {
            conversation = await _dbContext.ProjectConversations.SingleOrDefaultAsync(item =>
                item.Id == conversationId.Value && item.CreateBy == user
            );
            if (conversation != null)
            {
                var existingContextId = ContextIdUtil.NormalizeContextId(conversation.ContextId);
                if (
                    conversation.ProjectId != projectId
                    || !string.Equals(existingContextId, contextId, StringComparison.Ordinal)
                )
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        "The supplied conversation identity does not match the execution context."
                    );
                }

                conversation.ContextId = existingContextId;
            }
        }

        conversation ??= await _dbContext.ProjectConversations.SingleOrDefaultAsync(item =>
            item.ProjectId == projectId && item.ContextId == contextId && item.CreateBy == user
        );
        if (conversation == null && Guid.TryParse(contextId, out _))
        {
            var legacyContexts = await _dbContext
                .ProjectConversations.Where(item =>
                    item.ProjectId == projectId && item.ContextId.ToLower() == contextId && item.CreateBy == user
                )
                .ToListAsync();
            conversation = legacyContexts.OrderBy(item => item.CreateTime).FirstOrDefault();
            if (conversation != null)
            {
                conversation.ContextId = contextId;
            }
        }

        if (conversation != null && conversationId.HasValue && conversation.Id != conversationId.Value)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The supplied conversation identity does not match the execution context."
            );
        }

        if (conversation != null)
        {
            // A SignalR scope may still track the root from a turn preceding a reset on another process.
            await _dbContext.ProjectConversations.Entry(conversation).ReloadAsync();
            if (_dbContext.ProjectConversations.Entry(conversation).State == EntityState.Detached)
            {
                throw new AgwException(ErrorCodes.ResourceNotFound);
            }
            conversation.ContextId = contextId;
            UpdateExistingConversation(conversation, jobId, title, user, now);
            return (conversation, false);
        }

        conversation = new ProjectConversation
        {
            Id = conversationId ?? Guid.CreateVersion7(),
            ProjectId = projectId,
            JobId = jobId,
            ContextId = contextId,
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim(),
            CreateBy = user,
            CreateTime = now,
            UpdateBy = user,
            UpdateTime = now,
        };
        await _dbContext.ProjectConversations.AddAsync(conversation);
        return (conversation, true);
    }

    private static void UpdateExistingConversation(
        ProjectConversation conversation,
        Guid? jobId,
        string title,
        string user,
        DateTimeOffset now
    )
    {
        if (
            !string.IsNullOrWhiteSpace(title) && string.Equals(conversation.Title, "Untitled", StringComparison.Ordinal)
        )
        {
            conversation.Title = title;
        }

        if (conversation.JobId == null && jobId.HasValue)
        {
            conversation.JobId = jobId.Value;
        }

        conversation.UpdateBy = user;
        conversation.UpdateTime = now;
    }

    private async Task<ProjectConversation?> GetContextByTaskAsync(Guid projectId, Guid taskId)
    {
        var ownerUserId = ResolveOwnerUserId();
        var records = await GetRecordsByTaskIdAsync(taskId, ownerUserId);
        if (records.Count == 0)
        {
            return null;
        }

        var context = await _dbContext.ProjectConversations.SingleOrDefaultAsync(item =>
            item.Id == records[0].ConversationId && item.ProjectId == projectId && item.CreateBy == ownerUserId
        );
        return context;
    }

    private async Task<TaskProjection?> GetProjectedTaskAsync(Guid taskId, string? ownerUserId)
    {
        ownerUserId ??= ResolveOwnerUserId();
        var records = await GetOrderedRecordsByTaskIdAsync(taskId, ownerUserId);
        if (records.Count == 0)
        {
            return null;
        }

        var context = await _dbContext
            .ProjectConversations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == records[0].ConversationId && item.CreateBy == ownerUserId);
        return context == null ? null : TaskExecutionMapper.ToTask(context, records);
    }

    private async Task<IReadOnlyList<TaskProjection>> ListProjectedTasksAsync(string? ownerUserId)
    {
        var records = await _dbContext.ProjectConversationChatHistories.AsNoTracking().ToListAsync();
        if (records.Count == 0)
        {
            return [];
        }

        var contextIds = records.Select(record => record.ConversationId).ToHashSet();
        var contexts = await _dbContext
            .ProjectConversations.AsNoTracking()
            .Where(context => contextIds.Contains(context.Id) && context.CreateBy == ownerUserId)
            .ToListAsync();
        var contextById = contexts.ToDictionary(context => context.Id);

        return records
            .GroupBy(record => record.TaskId)
            .Where(group => contextById.ContainsKey(group.First().ConversationId))
            .Select(group => TaskExecutionMapper.ToTask(contextById[group.First().ConversationId], group.ToList()))
            .ToList();
    }

    private async Task<IReadOnlyList<ProjectConversationChatHistory>> GetOrderedRecordsByTaskIdAsync(
        Guid taskId,
        string? ownerUserId = null
    )
    {
        var records = await GetRecordsByTaskIdAsync(taskId, ownerUserId).ConfigureAwait(false);
        return ProjectConversationChatHistoryRules.Order(records);
    }

    private async Task<IReadOnlyList<ProjectConversationChatHistory>> GetRecordsByTaskIdAsync(
        Guid taskId,
        string? ownerUserId
    )
    {
        var records = await _dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record => record.TaskId == taskId)
            .ToListAsync();
        if (records.Count == 0)
        {
            return records;
        }

        var conversationIds = records.Select(record => record.ConversationId).Distinct().ToArray();
        var ownedConversationIds = await _dbContext
            .ProjectConversations.AsNoTracking()
            .Where(conversation => conversationIds.Contains(conversation.Id) && conversation.CreateBy == ownerUserId)
            .Select(conversation => conversation.Id)
            .ToHashSetAsync()
            .ConfigureAwait(false);
        return records.Where(record => ownedConversationIds.Contains(record.ConversationId)).ToList();
    }

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;
}
