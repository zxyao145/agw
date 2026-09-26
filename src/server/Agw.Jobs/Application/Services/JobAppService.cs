using Agw.Auth.Contracts;
using Agw.Jobs.Application.Persistence;
using Agw.Jobs.Contracts;
using Agw.Jobs.Domain.Behaviors;
using Agw.Jobs.Domain.Services;
using Agw.Jobs.Domain.ValueObjects;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Projects.Contracts.Execution;
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
    private readonly JobDefinitionDomainService _definitionDomainService;
    private readonly JobSchedulerWakeSignal _schedulerWakeSignal;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfoService;

    public JobAppService(
        IJobsDbContext dbContext,
        IProjectTaskFacade projectTasks,
        JobDefinitionDomainService definitionDomainService,
        JobSchedulerWakeSignal schedulerWakeSignal,
        TimeProvider timeProvider,
        IUserInfoService userInfoService,
        IApplicationLock? applicationLock = null
    )
    {
        _dbContext = dbContext;
        _projectTasks = projectTasks;
        _definitionDomainService = definitionDomainService;
        _schedulerWakeSignal = schedulerWakeSignal;
        _timeProvider = timeProvider;
        _userInfoService = userInfoService;
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
        var conversationIdByTaskId = await _projectTasks
            .ResolveConversationIdsAsync(taskIds, cancellationToken)
            .ConfigureAwait(false);

        return logs.OrderByDescending(log => log.StartTime)
            .Select(log => new JobLogResponse(
                log.Id,
                log.JobId,
                conversationIdByTaskId.TryGetValue(log.TaskId, out var conversationId) ? conversationId : null,
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

        await _definitionDomainService
            .EnsureTargetsVisibleAsync(request.ProjectId, request.AgentType, request.AgentId, user)
            .ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var definition = new JobDefinition
        {
            ProjectId = request.ProjectId,
            AgentType = request.AgentType,
            AgentId = request.AgentId,
            Name = await _definitionDomainService.ResolveNameAsync(request.Name, user, now),
            Prompt = request.Prompt,
            TriggerType = request.TriggerType,
            TriggerValue = request.TriggerValue,
            MaxRetryCount = request.MaxRetryCount,
            IsEnabled = request.IsEnabled,
        };
        var entity = new Job
        {
            Id = Guid.CreateVersion7(),
            CreateBy = user,
            CreateTime = now,
            UpdateBy = user,
            UpdateTime = now,
        };
        new JobBehavior(entity).Create(definition, now);

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

        await _definitionDomainService
            .EnsureTargetsVisibleAsync(request.ProjectId, request.AgentType, request.AgentId, user)
            .ConfigureAwait(false);
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

        await _definitionDomainService
            .EnsureTargetsVisibleAsync(projectId, request.AgentType, request.AgentId, user)
            .ConfigureAwait(false);
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
        var now = _timeProvider.GetUtcNow();
        var definition = new JobDefinition
        {
            ProjectId = request.ProjectId,
            AgentType = request.AgentType,
            AgentId = request.AgentId,
            Name = await _definitionDomainService.ResolveNameAsync(request.Name, user, now),
            Prompt = request.Prompt,
            TriggerType = request.TriggerType,
            TriggerValue = request.TriggerValue,
            MaxRetryCount = request.MaxRetryCount,
            IsEnabled = request.IsEnabled,
        };
        new JobBehavior(entity).Update(definition, request.Status, recalculateSchedule, now);
        entity.UpdateBy = user;
        entity.UpdateTime = now;

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

        new JobBehavior(entity).EnsureMutable();

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

        new JobBehavior(entity).EnsureMutable();

        await _dbContext
            .JobLogs.Where(log => log.JobId == entity.Id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        _dbContext.Jobs.Remove(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;
}
