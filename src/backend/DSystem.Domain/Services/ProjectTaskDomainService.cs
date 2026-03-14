using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;
using DSystem.Shared.Enums;
using DSystem.Shared.Models;
using System.Linq.Expressions;

namespace DSystem.Domain.Services;

public class ProjectTaskDomainService
{
    private readonly IRepository<ProjectTask> _taskRepository;
    private readonly IRepository<TaskRecord> _taskRecordRepository;
    private readonly IRepository<Project> _projectRepository;
    private readonly IRepository<Agentflow> _agentflowRepository;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ProjectTaskDomainService(
        IRepository<ProjectTask> taskRepository,
        IRepository<TaskRecord> taskRecordRepository,
        IRepository<Project> projectRepository,
        IRepository<Agentflow> agentflowRepository,
        IRepository<Agent> agentRepository,
        IUnitOfWork unitOfWork)
    {
        _taskRepository = taskRepository;
        _taskRecordRepository = taskRecordRepository;
        _projectRepository = projectRepository;
        _agentflowRepository = agentflowRepository;
        _agentRepository = agentRepository;
        _unitOfWork = unitOfWork;
    }

    public Task<IReadOnlyList<ProjectTask>> ListAsync(Expression<Func<ProjectTask, bool>>? predicate = null) =>
        _taskRepository.ListAsync(predicate);

    public Task<ProjectTask?> GetAsync(Guid id) => _taskRepository.GetByIdAsync(id);

    public async Task<ProjectTask?> CreateAsync(ProjectTask task, TaskRecord initialRecord, string user)
    {
        if (string.IsNullOrWhiteSpace(task.Description)
            || string.IsNullOrWhiteSpace(task.ContextId)
            || string.IsNullOrWhiteSpace(initialRecord.SessionId)
            || string.IsNullOrWhiteSpace(GetInputText(initialRecord.Input)))
        {
            return null;
        }

        if (Guid.TryParse(task.ProjectId, out var projectId))
        {
            var project = await _projectRepository.GetByIdAsync(projectId);
            if (project == null)
            {
                return null;
            }
        }

        if (!await IsTargetValidAsync(initialRecord.AgentType, initialRecord.AgentId))
        {
            return null;
        }

        task.Id = task.Id == Guid.Empty ? Guid.NewGuid() : task.Id;
        task.Title = task.Title?.Trim() ?? string.Empty;
        task.Description = task.Description.Trim();
        task.Status = ProjectTaskStatus.Pending;
        task.CreateBy = user;
        task.CreateTime = DateTime.UtcNow;
        task.UpdateBy = user;
        task.UpdateTime = task.CreateTime;

        initialRecord.Id = initialRecord.Id == Guid.Empty ? Guid.NewGuid() : initialRecord.Id;
        initialRecord.ContextId = task.ContextId;
        initialRecord.CreateBy = user;
        initialRecord.CreateTime = task.CreateTime;
        initialRecord.UpdateBy = user;
        initialRecord.UpdateTime = task.CreateTime;

        await _taskRepository.AddAsync(task);
        await _taskRecordRepository.AddAsync(initialRecord);
        await _unitOfWork.SaveChangesAsync();
        return task;
    }

    public async Task<ProjectTask?> UpdateAsync(Guid id, string description, string input, string user)
    {
        var existing = await _taskRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        var latestRecord = await GetLatestTaskRecordAsync(existing.ContextId);
        if (latestRecord == null)
        {
            return null;
        }

        existing.Description = description?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(existing.Description) || string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        latestRecord.Input = CreateUserInputMessage(input);
        latestRecord.UpdateBy = user;
        latestRecord.UpdateTime = DateTime.UtcNow;

        existing.UpdateBy = user;
        existing.UpdateTime = latestRecord.UpdateTime;

        _taskRepository.Update(existing);
        _taskRecordRepository.Update(latestRecord);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<ProjectTask?> UpdateTitleAsync(Guid id, string title, string user)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var existing = await _taskRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        existing.Title = title.Trim();
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
            return;
        }

        var records = await _taskRecordRepository.ListAsync(r => r.ContextId == existing.ContextId);
        foreach (var record in records)
        {
            _taskRecordRepository.Remove(record);
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
        var projectIdText = projectId.ToString("D");
        var pending = await _taskRepository.ListAsync(t =>
            t.ProjectId == projectIdText && t.Status == ProjectTaskStatus.Pending);

        return pending
            .OrderBy(t => t.UpdateTime ?? t.CreateTime)
            .ThenBy(t => t.CreateTime)
            .FirstOrDefault();
    }

    private async Task<bool> IsTargetValidAsync(ProjectTaskAgentType agentType, Guid? agentId)
    {
        if (!agentId.HasValue)
        {
            return false;
        }

        return agentType switch
        {
            ProjectTaskAgentType.Agentflow =>
                await _agentflowRepository.GetByIdAsync(agentId.Value) is Agentflow agentflow && agentflow.Enable,
            ProjectTaskAgentType.Agent =>
                await _agentRepository.GetByIdAsync(agentId.Value) is not null,
            _ => false
        };
    }

    private async Task<TaskRecord?> GetLatestTaskRecordAsync(string contextId)
    {
        var records = await _taskRecordRepository.ListAsync(r => r.ContextId == contextId);
        return records
            .OrderBy(r => r.UpdateTime ?? r.CreateTime)
            .ThenBy(r => r.CreateTime)
            .LastOrDefault();
    }

    private static UserInputMessage CreateUserInputMessage(string input)
    {
        return new UserInputMessage(
            [new AiMessageContent(AiMessageContentType.TextContent, input.Trim())]);
    }

    private static string GetInputText(UserInputMessage input)
    {
        return string.Concat(input.Contents.Select(content => content.Content?.ToString() ?? string.Empty));
    }
}
