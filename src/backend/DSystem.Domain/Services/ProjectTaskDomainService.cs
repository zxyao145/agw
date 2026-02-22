using DSystem.Domain.Entities;
using DSystem.Shared.Enums;
using DSystem.Domain.Repositories;
using System.Linq.Expressions;

namespace DSystem.Domain.Services;

public class ProjectTaskDomainService
{
    private readonly IRepository<ProjectTask> _taskRepository;
    private readonly IRepository<Project> _projectRepository;
    private readonly IRepository<Agentflow> _agentflowRepository;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ProjectTaskDomainService(
        IRepository<ProjectTask> taskRepository,
        IRepository<Project> projectRepository,
        IRepository<Agentflow> agentflowRepository,
        IRepository<Agent> agentRepository,
        IUnitOfWork unitOfWork)
    {
        _taskRepository = taskRepository;
        _projectRepository = projectRepository;
        _agentflowRepository = agentflowRepository;
        _agentRepository = agentRepository;
        _unitOfWork = unitOfWork;
    }

    public Task<IReadOnlyList<ProjectTask>> ListAsync(Expression<Func<ProjectTask, bool>>? predicate = null) =>
        _taskRepository.ListAsync(predicate);

    public Task<ProjectTask?> GetAsync(Guid id) => _taskRepository.GetByIdAsync(id);

    public async Task<ProjectTask?> CreateAsync(ProjectTask task, string user)
    {
        if (string.IsNullOrWhiteSpace(task.Description) || string.IsNullOrWhiteSpace(task.Input))
        {
            return null;
        }

        var project = await _projectRepository.GetByIdAsync(task.ProjectId);
        if (project == null || !project.Enable)
        {
            return null;
        }

        if (task.AgentType == ProjectTaskAgentType.Agentflow)
        {
            if (!task.AgentflowId.HasValue || task.AgentId.HasValue)
            {
                return null;
            }

            var agentflow = await _agentflowRepository.GetByIdAsync(task.AgentflowId.Value);
            if (agentflow == null || !agentflow.Enable)
            {
                return null;
            }
        }
        else if (task.AgentType == ProjectTaskAgentType.Agent)
        {
            if (!task.AgentId.HasValue || task.AgentflowId.HasValue)
            {
                return null;
            }

            var agent = await _agentRepository.GetByIdAsync(task.AgentId.Value);
            if (agent == null)
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        task.Id = task.Id == Guid.Empty ? Guid.NewGuid() : task.Id;
        task.Status = ProjectTaskStatus.Pending;

        task.CreateBy = user;
        task.CreateTime = DateTime.UtcNow;

        // Use UpdateTime as the ordering key. Initialize it on creation so it is never null.
        task.UpdateBy = user;
        task.UpdateTime = task.CreateTime;

        await _taskRepository.AddAsync(task);
        await _unitOfWork.SaveChangesAsync();
        return task;
    }

    public async Task<ProjectTask?> UpdateAsync(Guid id, Action<ProjectTask> updateAction, string user)
    {
        var existing = await _taskRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        updateAction(existing);

        if (string.IsNullOrWhiteSpace(existing.Description) || string.IsNullOrWhiteSpace(existing.Input))
        {
            return null;
        }

        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;
        _taskRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<ProjectTask?> ReorderAsync(Guid id, DateTime newUpdateTimeUtc, string user)
    {
        var existing = await _taskRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        if (existing.Status != ProjectTaskStatus.Pending)
        {
            return null;
        }

        existing.UpdateBy = user;
        existing.UpdateTime = newUpdateTimeUtc;
        _taskRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<ProjectTask?> CancelAsync(Guid id, string user)
    {
        var existing = await _taskRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        if (existing.Status is not (ProjectTaskStatus.Pending or ProjectTaskStatus.Running))
        {
            return null;
        }

        existing.Status = ProjectTaskStatus.Canceled;
        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;
        existing.FinishedTime = DateTime.UtcNow;

        _taskRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteAsync(Guid id, string user)
    {
        var existing = await _taskRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return ;
        }

        _taskRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<ProjectTask?> TryMarkRunningAsync(Guid id, string user)
    {
        var existing = await _taskRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        if (existing.Status != ProjectTaskStatus.Pending)
        {
            return null;
        }

        existing.Status = ProjectTaskStatus.Running;
        existing.StartedTime = DateTime.UtcNow;
        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;

        _taskRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<ProjectTask?> MarkSucceededAsync(Guid id, string user)
    {
        var existing = await _taskRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        if (existing.Status != ProjectTaskStatus.Running)
        {
            return null;
        }

        existing.Status = ProjectTaskStatus.Succeeded;
        existing.ErrorMessage = null;
        existing.FinishedTime = DateTime.UtcNow;
        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;

        _taskRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<ProjectTask?> MarkFailedAsync(Guid id, string errorMessage, string user)
    {
        var existing = await _taskRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        if (existing.Status != ProjectTaskStatus.Running)
        {
            return null;
        }

        existing.Status = ProjectTaskStatus.Failed;
        existing.ErrorMessage = errorMessage;
        existing.FinishedTime = DateTime.UtcNow;
        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;

        _taskRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<ProjectTask?> GetNextPendingAsync(Guid projectId)
    {
        // NOTE: Generic repository doesn't support ordering; we do in-memory ordering for now.
        // This is acceptable for initial skeleton and can be optimized later with a specialized repository.
        var pending = await _taskRepository.ListAsync(t =>
            t.ProjectId == projectId && t.Status == ProjectTaskStatus.Pending);

        return pending
            .OrderBy(t => t.UpdateTime ?? t.CreateTime)
            .ThenBy(t => t.CreateTime)
            .FirstOrDefault();
    }
}

