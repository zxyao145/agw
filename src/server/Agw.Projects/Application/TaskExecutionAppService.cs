using System.Linq.Expressions;
using Agw.Projects.Domain.Services;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public class TaskExecutionAppService
{
    private readonly IRepository<ProjectConversation> _contextRepository;
    private readonly IRepository<ProjectConversationChatHistory> _recordRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ProjectConversationChatHistoryDomainService _chatHistoryDomainService;
    private readonly ProjectResolver _projectResolver;
    private readonly TimeProvider _timeProvider;

    public TaskExecutionAppService(
        IRepository<ProjectConversation> contextRepository,
        IRepository<ProjectConversationChatHistory> recordRepository,
        IUnitOfWork unitOfWork,
        ProjectConversationChatHistoryDomainService chatHistoryDomainService,
        ProjectResolver projectResolver,
        TimeProvider timeProvider
    )
    {
        _contextRepository = contextRepository;
        _recordRepository = recordRepository;
        _unitOfWork = unitOfWork;
        _chatHistoryDomainService = chatHistoryDomainService;
        _projectResolver = projectResolver;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<TaskProjection>> ListAsync(Expression<Func<TaskProjection, bool>>? predicate = null)
    {
        var tasks = await ListProjectedTasksAsync();
        return predicate == null ? tasks : tasks.AsQueryable().Where(predicate).ToList();
    }

    public Task<TaskProjection?> GetTaskAsync(Guid id) => GetProjectedTaskAsync(id);

    public async Task<IReadOnlyList<TaskExecutionSummary>> ListResponsesAsync(Guid projectId)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return [];
        }

        var contexts = await _contextRepository.ListAsync(context => context.ProjectId == project.Id);
        if (contexts.Count == 0)
        {
            return [];
        }

        var contextIds = contexts.Select(context => context.Id).ToHashSet();
        var records = await _recordRepository.ListAsync(record => contextIds.Contains(record.ConversationId));
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

        var records = await GetOrderedRecordsByTaskIdAsync(taskId);
        if (records.Count == 0)
        {
            return null;
        }

        var context = await _contextRepository.GetByIdAsync(records[0].ConversationId);
        if (context == null || context.ProjectId != project.Id)
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
        return CreateAsync(projectId, request, user, TaskExecutionStatus.Pending, taskId);
    }

    /// <summary>
    /// 创建任务上下文和初始记录，并统一解析请求中的 context ID。
    /// </summary>
    private async Task<ApplicationResult<TaskExecutionSnapshot>> CreateAsync(
        Guid projectId,
        TaskCreateRequest request,
        string user,
        TaskExecutionStatus initialStatus,
        Guid? taskIdOverride = null
    )
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
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

        var context = await GetOrCreateContextAsync(project.Id, contextId, request.JobId, title, user, now);
        var record = new ProjectConversationChatHistory
        {
            Id = Guid.CreateVersion7(),
            ConversationId = context.Id,
            TaskId = taskId,
            JobId = request.JobId,
            Status = initialStatus,
            CreateTime = now,
            UpdateTime = now,
        };

        await _recordRepository.AddAsync(record);
        await _unitOfWork.SaveChangesAsync();

        var task = TaskExecutionMapper.ToTask(context, [record]);
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
        _contextRepository.Update(context);
        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<ApplicationResult> DeleteTaskAsync(Guid projectId, Guid taskId)
    {
        var context = await GetContextByTaskAsync(projectId, taskId);
        if (context == null)
        {
            return ApplicationResult.Success();
        }

        await _recordRepository.Queryable.Where(x => x.TaskId == taskId).ExecuteDeleteAsync();

        await _unitOfWork.SaveChangesAsync();

        return ApplicationResult.Success();
    }

    public async Task<ApplicationResult> ClearRecordsAsync(Guid projectId, Guid taskId)
    {
        var context = await GetContextByTaskAsync(projectId, taskId);
        if (context == null)
        {
            return ApplicationResult.NotFound();
        }

        await _recordRepository.Queryable.Where(x => x.TaskId == taskId).ExecuteDeleteAsync();

        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<ProjectConversationChatHistory?> GetLatestRecordAsync(Guid taskId)
    {
        var records = await GetOrderedRecordsByTaskIdAsync(taskId);
        return _chatHistoryDomainService.GetLatest(records);
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
        var records = _chatHistoryDomainService.Order(
            await _recordRepository.Queryable.Where(record => record.TaskId == id).ToListAsync()
        );
        if (records.Count == 0)
        {
            return null;
        }

        var context = await _contextRepository.GetByIdAsync(records[0].ConversationId);
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

        context.UpdateBy = user;
        context.UpdateTime = now;
        _contextRepository.Update(context);
        await _unitOfWork.SaveChangesAsync();

        return TaskExecutionMapper.ToTask(context, records);
    }

    /// <summary>
    /// 获取或创建项目上下文，并复用及规范化 SQLite 中仅 GUID 大小写不同的旧记录。
    /// </summary>
    private async Task<ProjectConversation> GetOrCreateContextAsync(
        Guid projectId,
        string contextId,
        Guid? jobId,
        string title,
        string user,
        DateTimeOffset now
    )
    {
        var context = await _contextRepository.SingleOrDefaultAsync(item =>
            item.ProjectId == projectId && item.ContextId == contextId
        );
        if (context == null && Guid.TryParse(contextId, out _))
        {
            var legacyContexts = await _contextRepository.ListAsync(item =>
                item.ProjectId == projectId && item.ContextId.ToLower() == contextId
            );
            context = legacyContexts.OrderBy(item => item.CreateTime).FirstOrDefault();
            if (context != null)
            {
                context.ContextId = contextId;
            }
        }

        if (context != null)
        {
            if (!string.IsNullOrWhiteSpace(title) && string.Equals(context.Title, "Untitled", StringComparison.Ordinal))
            {
                context.Title = title;
            }

            if (context.JobId == null && jobId.HasValue)
            {
                context.JobId = jobId.Value;
            }

            context.UpdateBy = user;
            context.UpdateTime = now;
            _contextRepository.Update(context);
            return context;
        }

        context = new ProjectConversation
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            JobId = jobId,
            ContextId = contextId,
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim(),
            CreateBy = user,
            CreateTime = now,
            UpdateBy = user,
            UpdateTime = now,
        };
        await _contextRepository.AddAsync(context);
        return context;
    }

    private async Task<ProjectConversation?> GetContextByTaskAsync(Guid projectId, Guid taskId)
    {
        var records = await _recordRepository.ListAsync(record => record.TaskId == taskId);
        if (records.Count == 0)
        {
            return null;
        }

        var context = await _contextRepository.GetByIdAsync(records[0].ConversationId);
        return context != null && context.ProjectId == projectId ? context : null;
    }

    private async Task<TaskProjection?> GetProjectedTaskAsync(Guid taskId)
    {
        var records = await GetOrderedRecordsByTaskIdAsync(taskId);
        if (records.Count == 0)
        {
            return null;
        }

        var context = await _contextRepository.GetByIdAsync(records[0].ConversationId);
        return context == null ? null : TaskExecutionMapper.ToTask(context, records);
    }

    private async Task<IReadOnlyList<TaskProjection>> ListProjectedTasksAsync()
    {
        var records = await _recordRepository.ListAsync();
        if (records.Count == 0)
        {
            return [];
        }

        var contextIds = records.Select(record => record.ConversationId).ToHashSet();
        var contexts = await _contextRepository.ListAsync(context => contextIds.Contains(context.Id));
        var contextById = contexts.ToDictionary(context => context.Id);

        return records
            .GroupBy(record => record.TaskId)
            .Where(group => contextById.ContainsKey(group.First().ConversationId))
            .Select(group => TaskExecutionMapper.ToTask(contextById[group.First().ConversationId], group.ToList()))
            .ToList();
    }

    private async Task<IReadOnlyList<ProjectConversationChatHistory>> GetOrderedRecordsByTaskIdAsync(Guid taskId)
    {
        var records = await _recordRepository.ListAsync(record => record.TaskId == taskId);
        return _chatHistoryDomainService.Order(records);
    }
}
