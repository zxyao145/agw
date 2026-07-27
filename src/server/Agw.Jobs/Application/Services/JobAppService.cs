using System.Globalization;

using Agw.Jobs.Application.Contracts;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;

using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Application.Services;

public class JobAppService
{
    private readonly IRepository<Job> _jobTaskRepository;
    private readonly IRepository<JobLog> _jobExecutionLogRepository;
    private readonly IRepository<TaskRecord> _taskRecordRepository;
    private readonly IRepository<ProjectContext> _projectContextRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly JobScheduleCalculator _jobScheduleCalculator;
    private readonly JobSchedulerWakeSignal _schedulerWakeSignal;
    private readonly TimeProvider _timeProvider;

    public JobAppService(
        IRepository<Job> jobTaskRepository,
        IRepository<JobLog> jobExecutionLogRepository,
        IRepository<TaskRecord> taskRecordRepository,
        IRepository<ProjectContext> projectContextRepository,
        IUnitOfWork unitOfWork,
        JobScheduleCalculator jobScheduleCalculator,
        JobSchedulerWakeSignal schedulerWakeSignal,
        TimeProvider timeProvider)
    {
        _jobTaskRepository = jobTaskRepository;
        _jobExecutionLogRepository = jobExecutionLogRepository;
        _taskRecordRepository = taskRecordRepository;
        _projectContextRepository = projectContextRepository;
        _unitOfWork = unitOfWork;
        _jobScheduleCalculator = jobScheduleCalculator;
        _schedulerWakeSignal = schedulerWakeSignal;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default)
    {
        var jobs = await _jobTaskRepository.Queryable
            .ToListAsync(cancellationToken);

        return jobs
            .OrderBy(t => t.NextRunTime)
            .ToList();
    }

    public async Task<IReadOnlyList<Job>> ListByProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var jobs = await _jobTaskRepository.Queryable
            .Where(job => job.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        return jobs
            .OrderBy(job => job.NextRunTime)
            .ToList();
    }

    public Task<Job?> GetAsync(Guid id)
    {
        return _jobTaskRepository.GetByIdAsync(id);
    }

    public Task<Job?> GetByProjectAsync(
        Guid id,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        return _jobTaskRepository.Queryable
            .SingleOrDefaultAsync(
                job => job.Id == id && job.ProjectId == projectId,
                cancellationToken);
    }

    public async Task<IReadOnlyList<JobLogResponse>> ListLogsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var logs = await _jobExecutionLogRepository.Queryable
            .Where(log => log.JobId == jobId)
            .ToListAsync(cancellationToken);

        if (logs.Count == 0)
        {
            return [];
        }

        var taskIds = logs.Select(log => log.TaskId).ToHashSet();
        var taskRecords = await _taskRecordRepository.Queryable
            .AsNoTracking()
            .Where(record => taskIds.Contains(record.TaskId))
            .ToListAsync(cancellationToken);
        var contextIds = taskRecords.Select(record => record.ProjectContextId).ToHashSet();
        var contexts = await _projectContextRepository.Queryable
            .AsNoTracking()
            .Where(context => contextIds.Contains(context.Id))
            .ToListAsync(cancellationToken);
        var contextIdByTaskId = taskRecords
            .GroupBy(record => record.TaskId)
            .ToDictionary(
                group => group.Key,
                group => contexts.FirstOrDefault(context => context.Id == group.First().ProjectContextId)?.ContextId);

        return logs
            .OrderByDescending(log => log.StartTime)
            .Select(log => new JobLogResponse(
                log.Id,
                log.JobId,
                contextIdByTaskId.GetValueOrDefault(log.TaskId),
                log.StartTime,
                log.EndTime,
                log.Success,
                log.Attempt,
                log.ErrorMessage))
            .ToList();
    }

    public async Task<Job> CreateAsync(JobCreateRequest request, string user)
    {
        var now = _timeProvider.GetUtcNow();
        var entity = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = request.ProjectId,
            AgentType = request.AgentType,
            AgentId = request.AgentId,
            Name = await ResolveNameAsync(request.Name, now),
            Prompt = request.Prompt,
            TriggerType = request.TriggerType,
            TriggerValue = request.TriggerValue,
            NextRunTime = now,
            MaxRetryCount = request.MaxRetryCount,
            IsEnabled = request.IsEnabled,
            Status = JobStatus.Pending,
            CreateBy = user,
            CreateTime = now,
            UpdateBy = user,
            UpdateTime = now
        };
        entity.NextRunTime = ResolveNextRunTime(entity, now);

        await _jobTaskRepository.AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();
        _schedulerWakeSignal.NotifyCreated(entity);
        return entity;
    }

    public async Task<Job?> UpdateAsync(Guid id, JobUpdateRequest request, string user)
    {
        var entity = await _jobTaskRepository.GetByIdAsync(id);
        if (entity == null)
        {
            return null;
        }

        return await UpdateEntityAsync(
            entity,
            request,
            user,
            recalculateSchedule: true);
    }

    public async Task<Job?> UpdateByProjectAsync(
        Guid id,
        Guid projectId,
        JobUpdateRequest request,
        string user,
        bool recalculateSchedule,
        CancellationToken cancellationToken = default)
    {
        var entity = await GetByProjectAsync(id, projectId, cancellationToken);
        if (entity == null)
        {
            return null;
        }

        request.ProjectId = projectId;
        return await UpdateEntityAsync(
            entity,
            request,
            user,
            recalculateSchedule,
            cancellationToken);
    }

    private async Task<Job> UpdateEntityAsync(
        Job entity,
        JobUpdateRequest request,
        string user,
        bool recalculateSchedule,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var nextRunTime = entity.NextRunTime;
        entity.ProjectId = request.ProjectId;
        entity.AgentType = request.AgentType;
        entity.AgentId = request.AgentId;
        entity.Name = await ResolveNameAsync(request.Name, now);
        entity.Prompt = request.Prompt;
        entity.TriggerType = request.TriggerType;
        entity.TriggerValue = request.TriggerValue;
        entity.MaxRetryCount = request.MaxRetryCount;
        entity.IsEnabled = request.IsEnabled;
        entity.Status = request.Status;
        entity.UpdateBy = user;
        entity.UpdateTime = now;
        entity.NextRunTime = recalculateSchedule
            ? ResolveNextRunTime(entity, now)
            : nextRunTime;

        _jobTaskRepository.Update(entity);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<Job?> UpdateEnabledAsync(JobEnabledUpdateRequest request, string user)
    {
        var entity = await _jobTaskRepository.GetByIdAsync(request.JobId);
        if (entity == null)
        {
            return null;
        }

        entity.IsEnabled = request.IsEnabled;
        entity.UpdateBy = user;
        entity.UpdateTime = _timeProvider.GetUtcNow();

        _jobTaskRepository.Update(entity);
        await _unitOfWork.SaveChangesAsync();

        if (entity.IsEnabled)
        {
            _schedulerWakeSignal.NotifyChanged();
        }

        return entity;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var entity = await _jobTaskRepository.GetByIdAsync(id);
        if (entity == null)
        {
            return false;
        }

        _jobTaskRepository.Remove(entity);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<Job?> DeleteByProjectAsync(
        Guid id,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var entity = await GetByProjectAsync(id, projectId, cancellationToken);
        if (entity == null)
        {
            return null;
        }

        _jobTaskRepository.Remove(entity);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return entity;
    }

    private async Task<string> ResolveNameAsync(string? requestedName, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            return requestedName.Trim();
        }

        var count = await _jobTaskRepository.Queryable.CountAsync();
        return $"job-{count + 1}-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
    }

    private DateTimeOffset ResolveNextRunTime(Job entity, DateTimeOffset now)
    {
        var nextRunTime = _jobScheduleCalculator.GetNextRunTime(entity, now);
        if (!nextRunTime.HasValue)
        {
            return DateTimeOffset.MaxValue;
        }

        return nextRunTime.Value;
    }
}
