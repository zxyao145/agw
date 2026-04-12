using Agw.Jobs.Contracts;
using Agw.Jobs.Domain.Entities;
using Agw.Jobs.Domain.Enums;
using Agw.Jobs.Domain.Events;
using Agw.Shared.Data.Repositories;

using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Application.Services;

public class JobAppService(
    IRepository<Job> jobTaskRepository,
    IRepository<JobLog> jobExecutionLogRepository,
    IUnitOfWork unitOfWork,
    IJobTimeCalculator jobTimeCalculator,
    IJobDomainEventDispatcher jobDomainEventDispatcher)
{
    public async Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default)
    {
        var jobs = await jobTaskRepository.Queryable
            .ToListAsync(cancellationToken);

        return jobs
            .OrderBy(t => t.NextRunTime)
            .ToList();
    }

    public Task<Job?> GetAsync(Guid id)
    {
        return jobTaskRepository.GetByIdAsync(id);
    }

    public async Task<IReadOnlyList<JobLog>> ListLogsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var logs = await jobExecutionLogRepository.Queryable
            .Where(log => log.JobId == jobId)
            .ToListAsync(cancellationToken);

        return logs
            .OrderByDescending(log => log.StartTime)
            .ToList();
    }

    public async Task<Job> CreateAsync(JobCreateRequest request, string user)
    {
        var now = DateTime.UtcNow;
        var entity = new Job
        {
            Id = Guid.NewGuid(),
            ProjectId = request.ProjectId,
            AgentType = request.AgentType,
            AgentId = request.AgentId,
            Name = request.Name,
            Prompt = request.Prompt,
            TriggerType = request.TriggerType,
            TriggerValue = request.TriggerValue,
            NextRunTime = DateTimeOffset.UtcNow,
            MaxRetryCount = request.MaxRetryCount,
            IsEnabled = request.IsEnabled,
            Status = JobStatus.Pending,
            CreateBy = user,
            CreateTime = now,
            UpdateBy = user,
            UpdateTime = now
        };
        entity.NextRunTime = ResolveNextRunTime(entity);

        await jobTaskRepository.AddAsync(entity);
        await unitOfWork.SaveChangesAsync();
        await jobDomainEventDispatcher.DispatchAsync(new JobCreatedDomainEvent(entity));
        return entity;
    }

    public async Task<Job?> UpdateAsync(Guid id, JobUpdateRequest request, string user)
    {
        var entity = await jobTaskRepository.GetByIdAsync(id);
        if (entity == null)
        {
            return null;
        }

        entity.ProjectId = request.ProjectId;
        entity.AgentType = request.AgentType;
        entity.AgentId = request.AgentId;
        entity.Name = request.Name;
        entity.Prompt = request.Prompt;
        entity.TriggerType = request.TriggerType;
        entity.TriggerValue = request.TriggerValue;
        entity.MaxRetryCount = request.MaxRetryCount;
        entity.IsEnabled = request.IsEnabled;
        entity.Status = request.Status;
        entity.UpdateBy = user;
        entity.UpdateTime = DateTime.UtcNow;
        entity.NextRunTime = ResolveNextRunTime(entity);

        jobTaskRepository.Update(entity);
        await unitOfWork.SaveChangesAsync();
        return entity;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var entity = await jobTaskRepository.GetByIdAsync(id);
        if (entity == null)
        {
            return false;
        }

        jobTaskRepository.Remove(entity);
        await unitOfWork.SaveChangesAsync();
        return true;
    }

    private DateTimeOffset ResolveNextRunTime(Job entity)
    {
        var nextRunTime = jobTimeCalculator.GetNextRunTime(entity, DateTimeOffset.UtcNow);
        if (!nextRunTime.HasValue)
        {
            return DateTimeOffset.MaxValue;
        }

        return nextRunTime.Value;
    }
}
