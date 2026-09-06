using System.Globalization;
using Agw.Agents.Contracts.Catalog;
using Agw.Auth.Contracts;
using Agw.Jobs.Application.Contracts;
using Agw.Jobs.Application.Persistence;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Application.Services;

public class JobAppService
{
    private readonly IJobsDbContext _dbContext;
    private readonly IApplicationLock _applicationLock;
    private readonly IProjectTaskFacade _projectTasks;
    private readonly JobScheduleCalculator _jobScheduleCalculator;
    private readonly JobSchedulerWakeSignal _schedulerWakeSignal;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfoService;
    private readonly IProjectRuntimeFacade _projects;
    private readonly IAgentCatalogFacade _agentCatalog;

    public JobAppService(
        IJobsDbContext dbContext,
        IProjectTaskFacade projectTasks,
        JobScheduleCalculator jobScheduleCalculator,
        JobSchedulerWakeSignal schedulerWakeSignal,
        TimeProvider timeProvider,
        IUserInfoService userInfoService,
        IProjectRuntimeFacade projects,
        IAgentCatalogFacade agentCatalog,
        IApplicationLock? applicationLock = null
    )
    {
        _dbContext = dbContext;
        _projectTasks = projectTasks;
        _jobScheduleCalculator = jobScheduleCalculator;
        _schedulerWakeSignal = schedulerWakeSignal;
        _timeProvider = timeProvider;
        _userInfoService = userInfoService;
        _projects = projects;
        _agentCatalog = agentCatalog;
        _applicationLock = applicationLock ?? InMemoryApplicationLock.Shared;
    }

    public async Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        var jobs = await _dbContext
            .Jobs.AsNoTracking()
            .Where(job => job.CreateBy == ownerUserId)
            .ToListAsync(cancellationToken);

        return jobs.OrderBy(t => t.NextRunTime).ToList();
    }

    public async Task<IReadOnlyList<Job>> ListByProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = ResolveOwnerUserId();
        var jobs = await _dbContext
            .Jobs.AsNoTracking()
            .Where(job => job.ProjectId == projectId && job.CreateBy == ownerUserId)
            .ToListAsync(cancellationToken);

        return jobs.OrderBy(job => job.NextRunTime).ToList();
    }

    public Task<Job?> GetAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        return _dbContext.Jobs.AsNoTracking().FirstOrDefaultAsync(job => job.Id == id && job.CreateBy == ownerUserId);
    }

    public Task<Job?> GetByProjectAsync(Guid id, Guid projectId, CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        return _dbContext.Jobs.SingleOrDefaultAsync(
            job => job.Id == id && job.ProjectId == projectId && job.CreateBy == ownerUserId,
            cancellationToken
        );
    }

    public async Task<IReadOnlyList<JobLogResponse>> ListLogsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = ResolveOwnerUserId();
        var jobExists = await _dbContext.Jobs.AnyAsync(
            job => job.Id == jobId && job.CreateBy == ownerUserId,
            cancellationToken
        );
        if (!jobExists)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        var logs = await _dbContext
            .JobLogs.AsNoTracking()
            .Where(log => log.JobId == jobId)
            .ToListAsync(cancellationToken);

        if (logs.Count == 0)
        {
            return [];
        }

        var taskIds = logs.Select(log => log.TaskId).ToHashSet();
        var contextIdByTaskId = await _projectTasks
            .ResolveContextIdsAsync(taskIds, cancellationToken)
            .ConfigureAwait(false);

        return logs.OrderByDescending(log => log.StartTime)
            .Select(log => new JobLogResponse(
                log.Id,
                log.JobId,
                contextIdByTaskId.GetValueOrDefault(log.TaskId),
                log.StartTime,
                log.EndTime,
                log.Success,
                log.Attempt,
                log.ErrorMessage
            ))
            .ToList();
    }

    public async Task<Job> CreateAsync(
        JobCreateRequest request,
        string user,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await _applicationLock.AcquireAsync(
            AgentDefinitionLock.GetResourceName(ResolveOwnerUserId()),
            cancellationToken
        );
        using var mutation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.HandleLostToken);
        cancellationToken = mutation.Token;

        await EnsureProjectVisibleAsync(request.ProjectId, user).ConfigureAwait(false);
        await EnsureAgentTargetVisibleAsync(request.AgentType, request.AgentId, user).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var entity = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = request.ProjectId,
            AgentType = request.AgentType,
            AgentId = request.AgentId,
            Name = await ResolveNameAsync(request.Name, user, now),
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
            UpdateTime = now,
        };
        entity.NextRunTime = ResolveNextRunTime(entity, now);

        await _dbContext.Jobs.AddAsync(entity, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _schedulerWakeSignal.NotifyCreated(entity);
        return entity;
    }

    public async Task<Job?> UpdateAsync(
        Guid id,
        JobUpdateRequest request,
        string user,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await _applicationLock.AcquireAsync(
            AgentDefinitionLock.GetResourceName(ResolveOwnerUserId()),
            cancellationToken
        );
        using var mutation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.HandleLostToken);
        cancellationToken = mutation.Token;

        var entity = await _dbContext.Jobs.FirstOrDefaultAsync(job => job.Id == id && job.CreateBy == user);
        if (entity == null)
        {
            return null;
        }

        await EnsureProjectVisibleAsync(request.ProjectId, user).ConfigureAwait(false);
        await EnsureAgentTargetVisibleAsync(request.AgentType, request.AgentId, user).ConfigureAwait(false);
        return await UpdateEntityAsync(entity, request, user, recalculateSchedule: true, cancellationToken);
    }

    public async Task<Job?> UpdateByProjectAsync(
        Guid id,
        Guid projectId,
        JobUpdateRequest request,
        string user,
        bool recalculateSchedule,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await _applicationLock.AcquireAsync(
            AgentDefinitionLock.GetResourceName(ResolveOwnerUserId()),
            cancellationToken
        );
        using var mutation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.HandleLostToken);
        cancellationToken = mutation.Token;

        var entity = await GetByProjectAsync(id, projectId, cancellationToken);
        if (entity == null)
        {
            return null;
        }

        await EnsureProjectVisibleAsync(projectId, user).ConfigureAwait(false);
        await EnsureAgentTargetVisibleAsync(request.AgentType, request.AgentId, user).ConfigureAwait(false);
        request.ProjectId = projectId;
        return await UpdateEntityAsync(entity, request, user, recalculateSchedule, cancellationToken);
    }

    private async Task<Job> UpdateEntityAsync(
        Job entity,
        JobUpdateRequest request,
        string user,
        bool recalculateSchedule,
        CancellationToken cancellationToken = default
    )
    {
        EnsureMutable(entity);
        if (request.Status == JobStatus.Running)
        {
            throw new AgwException(
                ErrorCodes.JobActiveAttemptConflict,
                "Job Running status is owned by the scheduler."
            );
        }

        var now = _timeProvider.GetUtcNow();
        var nextRunTime = entity.NextRunTime;
        entity.ProjectId = request.ProjectId;
        entity.AgentType = request.AgentType;
        entity.AgentId = request.AgentId;
        entity.Name = await ResolveNameAsync(request.Name, user, now);
        entity.Prompt = request.Prompt;
        entity.TriggerType = request.TriggerType;
        entity.TriggerValue = request.TriggerValue;
        entity.MaxRetryCount = request.MaxRetryCount;
        entity.IsEnabled = request.IsEnabled;
        entity.Status = request.Status;
        entity.UpdateBy = user;
        entity.UpdateTime = now;
        entity.NextRunTime = recalculateSchedule ? ResolveNextRunTime(entity, now) : nextRunTime;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<Job?> UpdateEnabledAsync(JobEnabledUpdateRequest request, string user)
    {
        var entity = await _dbContext.Jobs.FirstOrDefaultAsync(job => job.Id == request.JobId && job.CreateBy == user);
        if (entity == null)
        {
            return null;
        }

        entity.IsEnabled = request.IsEnabled;
        entity.UpdateBy = user;
        entity.UpdateTime = _timeProvider.GetUtcNow();

        await _dbContext.SaveChangesAsync();

        if (entity.IsEnabled)
        {
            _schedulerWakeSignal.NotifyChanged();
        }

        return entity;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var user = ResolveOwnerUserId();
        var entity = await _dbContext.Jobs.FirstOrDefaultAsync(job => job.Id == id && job.CreateBy == user);
        if (entity == null)
        {
            return false;
        }

        EnsureMutable(entity);

        await _dbContext.JobLogs.Where(log => log.JobId == entity.Id).ExecuteDeleteAsync().ConfigureAwait(false);
        _dbContext.Jobs.Remove(entity);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<Job?> DeleteByProjectAsync(Guid id, Guid projectId, CancellationToken cancellationToken = default)
    {
        var entity = await GetByProjectAsync(id, projectId, cancellationToken);
        if (entity == null)
        {
            return null;
        }

        EnsureMutable(entity);

        await _dbContext
            .JobLogs.Where(log => log.JobId == entity.Id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        _dbContext.Jobs.Remove(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }

    private async Task<string> ResolveNameAsync(string? requestedName, string user, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            return requestedName.Trim();
        }

        var count = await _dbContext.Jobs.CountAsync(job => job.CreateBy == user);
        return $"job-{count + 1}-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
    }

    private static void EnsureMutable(Job job)
    {
        if (job.Status == JobStatus.Running || job.ActiveExecutionId.HasValue)
        {
            throw new AgwException(ErrorCodes.JobActiveAttemptConflict);
        }
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

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;

    private async Task EnsureProjectVisibleAsync(Guid projectId, string user)
    {
        var project = await _projects.GetForCurrentUserAsync(projectId).ConfigureAwait(false);
        if (project == null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }
    }

    private async Task EnsureAgentTargetVisibleAsync(AgentRuntimeType? agentType, Guid? agentId, string user)
    {
        if (!agentType.HasValue || !agentId.HasValue)
        {
            return;
        }

        if (!await _agentCatalog.IsOwnedTargetAsync(agentType.Value, agentId.Value, user).ConfigureAwait(false))
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
    }
}
