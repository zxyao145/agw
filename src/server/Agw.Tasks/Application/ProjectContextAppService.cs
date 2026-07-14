using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;
using Agw.Tasks.Domain.Services;

using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Application;

public class ProjectContextAppService
{
    private readonly IRepository<ProjectContext> _contextRepository;
    private readonly IRepository<TaskRecord> _recordRepository;
    private readonly IRepository<AgentflowTrace> _traceRepository;
    private readonly IRepository<AgentUsage> _usageRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ProjectResolver _projectResolver;
    private readonly TaskRecordDomainService _taskRecordDomainService;
    private readonly ITaskSessionBindingService _taskSessionBindingService;
    private readonly TimeProvider _timeProvider;

    public ProjectContextAppService(
        IRepository<ProjectContext> contextRepository,
        IRepository<TaskRecord> recordRepository,
        IRepository<AgentflowTrace> traceRepository,
        IRepository<AgentUsage> usageRepository,
        IUnitOfWork unitOfWork,
        ProjectResolver projectResolver,
        TaskRecordDomainService taskRecordDomainService,
        ITaskSessionBindingService taskSessionBindingService,
        TimeProvider timeProvider)
    {
        _contextRepository = contextRepository;
        _recordRepository = recordRepository;
        _traceRepository = traceRepository;
        _usageRepository = usageRepository;
        _unitOfWork = unitOfWork;
        _projectResolver = projectResolver;
        _taskRecordDomainService = taskRecordDomainService;
        _taskSessionBindingService = taskSessionBindingService;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<ProjectContextSummaryResponse>> ListResponsesAsync(Guid projectId)
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
        var records = await _recordRepository.ListAsync(record => contextIds.Contains(record.ProjectContextId));
        var recordsByContextId = records
            .GroupBy(record => record.ProjectContextId)
            .ToDictionary(group => group.Key, group => group.ToList());

        return contexts
            .Select(context => ToSummaryResponse(context, recordsByContextId.GetValueOrDefault(context.Id) ?? []))
            .Where(HasConversationMessages)
            .OrderByDescending(context => context.UpdateTime ?? context.CreateTime)
            .ToList();
    }

    /// <summary>
    /// 使用规范化 context ID 查询项目上下文并转换为响应模型。
    /// </summary>
    public async Task<ProjectContextResponse?> GetResponseAsync(Guid projectId, string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            return null;
        }

        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return null;
        }

        var normalizedContextId = ContextIdUtil.NormalizeContextId(contextId);
        var context = await _contextRepository.SingleOrDefaultAsync(item =>
            item.ProjectId == project.Id && item.ContextId == normalizedContextId);

        return context == null ? null : await ToResponseAsync(context);
    }

    public async Task<ApplicationResult> ClearRecordsAsync(Guid projectId, string contextId)
    {
        var context = await GetProjectContextAsync(projectId, contextId);
        if (context == null)
        {
            return ApplicationResult.NotFound();
        }

        await _recordRepository.Queryable
            .Where(record => record.ProjectContextId == context.Id)
            .ExecuteDeleteAsync();
        await _traceRepository.Queryable
            .Where(trace => trace.ProjectId == context.ProjectId && trace.ContextId == context.ContextId)
            .ExecuteDeleteAsync();

        await _taskSessionBindingService.DeleteByContextAsync(context.Id);

        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<ApplicationResult> UpdateTitleAsync(Guid projectId, string contextId, string title, string user)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return ApplicationResult.Invalid("title is required.");
        }

        var context = await GetProjectContextAsync(projectId, contextId);
        if (context == null)
        {
            return ApplicationResult.NotFound();
        }

        context.Title = title.Trim();
        context.UpdateBy = user;
        context.UpdateTime = _timeProvider.GetUtcNow();
        _contextRepository.Update(context);
        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<ApplicationResult> DeleteAllAsync(Guid projectId)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult.NotFound();
        }

        var contexts = await _contextRepository.ListAsync(context => context.ProjectId == project.Id);
        foreach (var context in contexts)
        {
            await _taskSessionBindingService.DeleteByContextAsync(context.Id);
        }

        await _traceRepository.Queryable
            .Where(trace => trace.ProjectId == project.Id)
            .ExecuteDeleteAsync();

        await _contextRepository.Queryable
            .Where(context => context.ProjectId == project.Id)
            .ExecuteDeleteAsync();

        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<bool> DeleteAsync(Guid projectId, string contextId)
    {
        var context = await GetProjectContextAsync(projectId, contextId);
        if (context == null)
        {
            return false;
        }

        await _recordRepository.Queryable
            .Where(record => record.ProjectContextId == context.Id)
            .ExecuteDeleteAsync();
        await _traceRepository.Queryable
            .Where(trace => trace.ProjectId == context.ProjectId && trace.ContextId == context.ContextId)
            .ExecuteDeleteAsync();

        await _taskSessionBindingService.DeleteByContextAsync(context.Id);

        _contextRepository.Remove(context);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    private async Task<ProjectContextResponse> ToResponseAsync(ProjectContext context)
    {
        var records = await _recordRepository.ListAsync(record => record.ProjectContextId == context.Id);
        var orderedTasks = records
            .GroupBy(record => record.TaskId)
            .Select(group => TaskExecutionMapper.ToTask(context, group.ToList()))
            .OrderBy(task => task.CreateTime)
            .ThenBy(task => task.UpdateTime ?? task.CreateTime)
            .ThenBy(task => task.TaskId)
            .ToList();
        var messages = _taskRecordDomainService.Order(records)
            .SelectMany(TaskExecutionMapper.ToAiMessages)
            .ToList();
        var latestTask = GetLatestTask(orderedTasks);
        var usage = await GetUsageAsync(context);

        return new ProjectContextResponse(
            context.ProjectId.Normalize(),
            context.ContextId,
            context.JobId,
            latestTask?.Status,
            orderedTasks.Count,
            messages.Count,
            context.CreateTime,
            context.UpdateTime,
            latestTask?.ErrorMessage,
            usage,
            messages);
    }

    private async Task<ProjectContextUsage> GetUsageAsync(ProjectContext context) =>
        await _usageRepository.Queryable
            .Where(usage => usage.ProjectId == context.ProjectId && usage.ContextId == context.ContextId)
            .GroupBy(_ => 1)
            .Select(group => new ProjectContextUsage
            {
                InputTokenCount = group.Sum(usage => usage.InputTokenCount),
                OutputTokenCount = group.Sum(usage => usage.OutputTokenCount),
                TotalTokenCount = group.Sum(usage => usage.TotalTokenCount),
                CachedInputTokenCount = group.Sum(usage => usage.CachedInputTokenCount),
                ReasoningTokenCount = group.Sum(usage => usage.ReasoningTokenCount)
            })
            .SingleOrDefaultAsync() ?? new ProjectContextUsage();

    private static ProjectContextSummaryResponse ToSummaryResponse(
        ProjectContext context,
        IReadOnlyList<TaskRecord> records)
    {
        var tasks = records
            .GroupBy(record => record.TaskId)
            .Select(group => TaskExecutionMapper.ToTask(context, group.ToList()))
            .ToList();
        var latestTask = GetLatestTask(tasks);
        var messageCount = records.Count(record => record.ToChatMessage() != null);

        return new ProjectContextSummaryResponse(
            context.ProjectId.Normalize(),
            context.ContextId,
            context.JobId,
            context.Title,
            latestTask?.Status,
            tasks.Count,
            messageCount,
            context.CreateTime,
            context.UpdateTime,
            latestTask?.ErrorMessage);
    }

    /// <summary>
    /// 在解析项目标识后，使用规范化 context ID 查询持久化项目上下文。
    /// </summary>
    private async Task<ProjectContext?> GetProjectContextAsync(Guid projectId, string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            return null;
        }

        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return null;
        }

        var normalizedContextId = ContextIdUtil.NormalizeContextId(contextId);
        return await _contextRepository.SingleOrDefaultAsync(context =>
            context.ProjectId == project.Id && context.ContextId == normalizedContextId);
    }

    private static TaskProjection? GetLatestTask(IEnumerable<TaskProjection> tasks) =>
        tasks
            .OrderByDescending(task => task.UpdateTime ?? task.CreateTime)
            .ThenByDescending(task => task.CreateTime)
            .ThenByDescending(task => task.TaskId)
            .FirstOrDefault();

    private static bool HasConversationMessages(ProjectContextSummaryResponse context) =>
        context.MessageCount > 0;
}
