using DSystem.Domain.Entities;
using DSystem.Domain.Enums;
using DSystem.Domain.Repositories;
using System.Linq.Expressions;

namespace DSystem.Domain.Services;

public class WorkflowDomainService
{
    private readonly IRepository<Workflow> _workflowRepository;
    private readonly IRepository<WorkflowAgent> _workflowAgentRepository;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IUnitOfWork _unitOfWork;

    public WorkflowDomainService(
        IRepository<Workflow> workflowRepository,
        IRepository<WorkflowAgent> workflowAgentRepository,
        IRepository<Agent> agentRepository,
        IUnitOfWork unitOfWork)
    {
        _workflowRepository = workflowRepository;
        _workflowAgentRepository = workflowAgentRepository;
        _agentRepository = agentRepository;
        _unitOfWork = unitOfWork;
    }

    public Task<IReadOnlyList<Workflow>> ListAsync(Expression<Func<Workflow, bool>>? predicate = null) =>
        _workflowRepository.ListAsync(predicate);

    public Task<Workflow?> GetAsync(Guid id) => _workflowRepository.GetByIdAsync(id);

    public Task<IReadOnlyList<WorkflowAgent>> ListAgentsAsync(Guid workflowId) =>
        _workflowAgentRepository.ListAsync(x => x.WorkflowId == workflowId);

    public async Task<Workflow?> CreateAsync(Workflow workflow, IReadOnlyList<WorkflowAgent> agents, string user)
    {
        if (string.IsNullOrWhiteSpace(workflow.Name))
        {
            return null;
        }

        workflow.Id = workflow.Id == Guid.Empty ? Guid.NewGuid() : workflow.Id;
        workflow.CreateBy = user;
        workflow.CreateTime = DateTime.UtcNow;

        var normalizedAgents = await ValidateAndNormalizeAgentsAsync(workflow.Pattern, agents, workflow.Id);
        if (normalizedAgents == null)
        {
            return null;
        }

        await _workflowRepository.AddAsync(workflow);
        foreach (var workflowAgent in normalizedAgents)
        {
            workflowAgent.CreateBy = user;
            workflowAgent.CreateTime = workflow.CreateTime;
            await _workflowAgentRepository.AddAsync(workflowAgent);
        }

        await _unitOfWork.SaveChangesAsync();
        return workflow;
    }

    public async Task<Workflow?> UpdateAsync(
        Guid id,
        Action<Workflow> updateAction,
        IReadOnlyList<WorkflowAgent>? agents,
        string user)
    {
        var existing = await _workflowRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        updateAction(existing);

        if (string.IsNullOrWhiteSpace(existing.Name))
        {
            return null;
        }

        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;

        _workflowRepository.Update(existing);

        if (agents != null)
        {
            var normalizedAgents = await ValidateAndNormalizeAgentsAsync(existing.Pattern, agents, existing.Id);
            if (normalizedAgents == null)
            {
                return null;
            }

            var current = await _workflowAgentRepository.ListAsync(x => x.WorkflowId == existing.Id);
            foreach (var item in current)
            {
                _workflowAgentRepository.Remove(item);
            }

            foreach (var workflowAgent in normalizedAgents)
            {
                workflowAgent.CreateBy ??= existing.CreateBy;
                workflowAgent.CreateTime = existing.CreateTime == default ? DateTime.UtcNow : existing.CreateTime;
                workflowAgent.UpdateBy = user;
                workflowAgent.UpdateTime = DateTime.UtcNow;
                await _workflowAgentRepository.AddAsync(workflowAgent);
            }
        }

        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await _workflowRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        _workflowRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    private async Task<IReadOnlyList<WorkflowAgent>?> ValidateAndNormalizeAgentsAsync(
        WorkflowOrchestrationPattern pattern,
        IReadOnlyList<WorkflowAgent> agents,
        Guid workflowId)
    {
        if (agents == null)
        {
            return Array.Empty<WorkflowAgent>();
        }

        if (pattern == WorkflowOrchestrationPattern.Sequential && agents.Count == 0)
        {
            return null;
        }

        var agentIds = agents.Select(x => x.AgentId).ToList();
        if (agentIds.Count == 0)
        {
            return Array.Empty<WorkflowAgent>();
        }

        if (agentIds.Any(x => x == Guid.Empty))
        {
            return null;
        }

        if (agentIds.Distinct().Count() != agentIds.Count)
        {
            return null;
        }

        if (agents.Any(x => x.Order < 0))
        {
            return null;
        }

        var orders = agents.Select(x => x.Order).ToList();
        if (orders.Distinct().Count() != orders.Count)
        {
            return null;
        }

        // Ensure all referenced agents exist.
        var existingAgents = await _agentRepository.ListAsync(a => agentIds.Contains(a.Id));
        if (existingAgents.Count != agentIds.Count)
        {
            return null;
        }

        // Normalize FK.
        var normalized = agents
            .Select(x => new WorkflowAgent
            {
                WorkflowId = workflowId,
                AgentId = x.AgentId,
                Order = x.Order,
                Role = x.Role
            })
            .ToList();

        return normalized;
    }
}