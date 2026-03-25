using Agw.Domain.Entities;
using Agw.Jobs.Contracts;
using Agw.Jobs.Enums;
using Agw.Shared.Abstractions.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Services;

public class JobAppService(
    IRepository<Job> jobTaskRepository,
    IRepository<JobLog> jobExecutionLogRepository,
    IUnitOfWork unitOfWork)
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

    public async Task<IReadOnlyList<JobLog>> ListLogsAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var logs = await jobExecutionLogRepository.Queryable
            .Where(log => log.TaskId == taskId)
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
            TimeZoneId = request.TimeZoneId,
            NextRunTime = request.NextRunTime,
            MaxRetryCount = request.MaxRetryCount,
            IsEnabled = request.IsEnabled,
            Status = JobStatus.Pending,
            CreateBy = user,
            CreateTime = now,
            UpdateBy = user,
            UpdateTime = now
        };

        await jobTaskRepository.AddAsync(entity);
        await unitOfWork.SaveChangesAsync();
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
        entity.TimeZoneId = request.TimeZoneId;
        entity.NextRunTime = request.NextRunTime;
        entity.MaxRetryCount = request.MaxRetryCount;
        entity.IsEnabled = request.IsEnabled;
        entity.Status = request.Status;
        entity.UpdateBy = user;
        entity.UpdateTime = DateTime.UtcNow;

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
}
