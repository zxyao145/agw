using Agw.Domain.Entities;
using Agw.Jobs.Contracts;
using Agw.Jobs.Enums;
using Agw.Shared.Abstractions.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Services;

public class ScheduledTaskAppService(
    IRepository<ScheduledTask> scheduledTaskRepository,
    IRepository<TaskExecutionLog> taskExecutionLogRepository,
    IUnitOfWork unitOfWork)
{
    public async Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await scheduledTaskRepository.Queryable
            .OrderBy(t => t.NextRunTime)
            .ToListAsync(cancellationToken);
    }

    public Task<ScheduledTask?> GetAsync(Guid id)
    {
        return scheduledTaskRepository.GetByIdAsync(id);
    }

    public async Task<IReadOnlyList<TaskExecutionLog>> ListLogsAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        return await taskExecutionLogRepository.Queryable
            .Where(log => log.TaskId == taskId)
            .OrderByDescending(log => log.StartTime)
            .ToListAsync(cancellationToken);
    }

    public async Task<ScheduledTask> CreateAsync(ScheduledTaskCreateRequest request, string user)
    {
        var now = DateTime.UtcNow;
        var entity = new ScheduledTask
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
            Status = ScheduledTaskStatus.Pending,
            CreateBy = user,
            CreateTime = now,
            UpdateBy = user,
            UpdateTime = now
        };

        await scheduledTaskRepository.AddAsync(entity);
        await unitOfWork.SaveChangesAsync();
        return entity;
    }

    public async Task<ScheduledTask?> UpdateAsync(Guid id, ScheduledTaskUpdateRequest request, string user)
    {
        var entity = await scheduledTaskRepository.GetByIdAsync(id);
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

        scheduledTaskRepository.Update(entity);
        await unitOfWork.SaveChangesAsync();
        return entity;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var entity = await scheduledTaskRepository.GetByIdAsync(id);
        if (entity == null)
        {
            return false;
        }

        scheduledTaskRepository.Remove(entity);
        await unitOfWork.SaveChangesAsync();
        return true;
    }
}
