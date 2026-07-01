using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Extensions;
using Agw.Tasks.Domain.Services;

using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Application;

public class ProjectContextAppService
{
    private readonly IRepository<ProjectContext> _contextRepository;
    private readonly IRepository<TaskRecord> _recordRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ProjectResolver _projectResolver;
    private readonly TaskRecordDomainService _taskRecordDomainService;

    public ProjectContextAppService(
        IRepository<ProjectContext> contextRepository,
        IRepository<TaskRecord> recordRepository,
        IUnitOfWork unitOfWork,
        ProjectResolver projectResolver,
        TaskRecordDomainService taskRecordDomainService)
    {
        _contextRepository = contextRepository;
        _recordRepository = recordRepository;
        _unitOfWork = unitOfWork;
        _projectResolver = projectResolver;
        _taskRecordDomainService = taskRecordDomainService;
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

        var normalizedContextId = contextId.Trim();
        var context = await _contextRepository.SingleOrDefaultAsync(item =>
            item.ProjectId == project.Id && item.ContextId == normalizedContextId);

        return context == null ? null : await ToResponseAsync(context);
    }

    public async Task<ProjectContextResponse?> GetResponseByTaskIdAsync(Guid projectId, Guid taskId)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return null;
        }

        var records = await _recordRepository.ListAsync(record => record.TaskId == taskId);
        if (records.Count == 0)
        {
            return null;
        }

        var context = await _contextRepository.GetByIdAsync(records[0].ProjectContextId);
        return context == null || context.ProjectId != project.Id ? null : await ToResponseAsync(context);
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
        context.UpdateTime = DateTime.UtcNow;
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

        _contextRepository.Remove(context);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    private async Task<ProjectContextResponse> ToResponseAsync(ProjectContext context)
    {
        var records = await _recordRepository.ListAsync(record => record.ProjectContextId == context.Id);
        var orderedTasks = records
            .GroupBy(record => record.TaskId)
            .Select(group => TaskResponseMapper.ToTask(context, group.ToList()))
            .OrderBy(task => task.CreateTime)
            .ThenBy(task => task.UpdateTime ?? task.CreateTime)
            .ThenBy(task => task.TaskId)
            .ToList();
        var recordsByTaskId = records
            .GroupBy(record => record.TaskId)
            .ToDictionary(
                group => group.Key,
                group => _taskRecordDomainService.Order(group));
        var messages = orderedTasks
            .SelectMany(task => recordsByTaskId.GetValueOrDefault(task.TaskId) ?? [])
            .SelectMany(TaskResponseMapper.ToAiMessages)
            .ToList();
        var latestTask = GetLatestTask(orderedTasks);

        return new ProjectContextResponse(
            context.ProjectId.Normalize(),
            context.ContextId,
            context.JobId,
            latestTask?.TaskId,
            orderedTasks.Select(TaskResponseMapper.ToSummaryResponse).ToList(),
            messages.Count,
            messages);
    }

    private static ProjectContextSummaryResponse ToSummaryResponse(
        ProjectContext context,
        IReadOnlyList<TaskRecord> records)
    {
        var tasks = records
            .GroupBy(record => record.TaskId)
            .Select(group => TaskResponseMapper.ToTask(context, group.ToList()))
            .ToList();
        var latestTask = GetLatestTask(tasks);
        var messageCount = records.Count(record => record.ToChatMessage() != null);

        return new ProjectContextSummaryResponse(
            context.ProjectId.Normalize(),
            context.ContextId,
            context.JobId,
            context.Title,
            latestTask?.TaskId,
            latestTask?.Status,
            tasks.Count,
            messageCount,
            context.CreateTime,
            context.UpdateTime,
            latestTask?.ErrorMessage);
    }

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

        var normalizedContextId = contextId.Trim();
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
