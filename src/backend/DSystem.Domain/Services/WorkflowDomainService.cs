using DSystem.Domain.Entities;
using DSystem.Domain.Enums;
using DSystem.Domain.Repositories;
using System.Linq.Expressions;

namespace DSystem.Domain.Services;

public class WorkflowDomainService
{
    private readonly IRepository<Workflow> _workflowRepository;
    private readonly IRepository<WorkflowNode> _workflowNodeRepository;
    private readonly IRepository<WorkflowEdge> _workflowEdgeRepository;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IUnitOfWork _unitOfWork;

    public WorkflowDomainService(
        IRepository<Workflow> workflowRepository,
        IRepository<WorkflowNode> workflowNodeRepository,
        IRepository<WorkflowEdge> workflowEdgeRepository,
        IRepository<Agent> agentRepository,
        IUnitOfWork unitOfWork)
    {
        _workflowRepository = workflowRepository;
        _workflowNodeRepository = workflowNodeRepository;
        _workflowEdgeRepository = workflowEdgeRepository;
        _agentRepository = agentRepository;
        _unitOfWork = unitOfWork;
    }

    public Task<IReadOnlyList<Workflow>> ListAsync(Expression<Func<Workflow, bool>>? predicate = null) =>
        _workflowRepository.ListAsync(predicate);

    public Task<Workflow?> GetAsync(Guid id) => _workflowRepository.GetByIdAsync(id);

    public Task<IReadOnlyList<WorkflowNode>> ListNodesAsync(Guid workflowId) =>
        _workflowNodeRepository.ListAsync(x => x.WorkflowId == workflowId);

    public Task<IReadOnlyList<WorkflowEdge>> ListEdgesAsync(Guid workflowId) =>
        _workflowEdgeRepository.ListAsync(x => x.WorkflowId == workflowId);

    public async Task<Workflow?> CreateAsync(
        Workflow workflow,
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdge> edges,
        string user)
    {
        if (string.IsNullOrWhiteSpace(workflow.Name))
        {
            return null;
        }

        workflow.Id = workflow.Id == Guid.Empty ? Guid.NewGuid() : workflow.Id;
        workflow.CreateBy = user;
        workflow.CreateTime = DateTime.UtcNow;

        var (normalizedNodes, normalizedEdges) = await ValidateAndNormalizeGraphAsync(
            workflow.Pattern, nodes, edges, workflow.Id);
        if (normalizedNodes == null || normalizedEdges == null)
        {
            return null;
        }

        await _workflowRepository.AddAsync(workflow);

        foreach (var node in normalizedNodes)
        {
            node.CreateBy = user;
            node.CreateTime = workflow.CreateTime;
            await _workflowNodeRepository.AddAsync(node);
        }

        foreach (var edge in normalizedEdges)
        {
            edge.CreateBy = user;
            edge.CreateTime = workflow.CreateTime;
            await _workflowEdgeRepository.AddAsync(edge);
        }

        await _unitOfWork.SaveChangesAsync();
        return workflow;
    }

    public async Task<Workflow?> UpdateAsync(
        Guid id,
        Action<Workflow> updateAction,
        IReadOnlyList<WorkflowNode>? nodes,
        IReadOnlyList<WorkflowEdge>? edges,
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

        if (nodes != null && edges != null)
        {
            var (normalizedNodes, normalizedEdges) = await ValidateAndNormalizeGraphAsync(
                existing.Pattern, nodes, edges, existing.Id);
            if (normalizedNodes == null || normalizedEdges == null)
            {
                return null;
            }

            // Remove existing nodes and edges
            var currentNodes = await _workflowNodeRepository.ListAsync(x => x.WorkflowId == existing.Id);
            foreach (var item in currentNodes)
            {
                _workflowNodeRepository.Remove(item);
            }

            var currentEdges = await _workflowEdgeRepository.ListAsync(x => x.WorkflowId == existing.Id);
            foreach (var item in currentEdges)
            {
                _workflowEdgeRepository.Remove(item);
            }

            // Add new nodes and edges
            foreach (var node in normalizedNodes)
            {
                node.CreateBy ??= existing.CreateBy;
                node.CreateTime = existing.CreateTime == default ? DateTime.UtcNow : existing.CreateTime;
                node.UpdateBy = user;
                node.UpdateTime = DateTime.UtcNow;
                await _workflowNodeRepository.AddAsync(node);
            }

            foreach (var edge in normalizedEdges)
            {
                edge.CreateBy ??= existing.CreateBy;
                edge.CreateTime = existing.CreateTime == default ? DateTime.UtcNow : existing.CreateTime;
                edge.UpdateBy = user;
                edge.UpdateTime = DateTime.UtcNow;
                await _workflowEdgeRepository.AddAsync(edge);
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

    private async Task<(IReadOnlyList<WorkflowNode>?, IReadOnlyList<WorkflowEdge>?)> ValidateAndNormalizeGraphAsync(
        WorkflowOrchestrationPattern pattern,
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdge> edges,
        Guid workflowId)
    {
        if (nodes == null || edges == null)
        {
            return (Array.Empty<WorkflowNode>(), Array.Empty<WorkflowEdge>());
        }

        if (pattern == WorkflowOrchestrationPattern.Sequential && nodes.Count == 0)
        {
            return (null, null);
        }

        // Validate nodes
        var nodeIds = nodes.Select(x => x.NodeId).ToList();
        if (nodeIds.Count == 0)
        {
            return (Array.Empty<WorkflowNode>(), Array.Empty<WorkflowEdge>());
        }

        // Ensure unique node IDs
        if (nodeIds.Distinct().Count() != nodeIds.Count)
        {
            return (null, null);
        }

        // Validate that all AgentNode references exist
        var agentNodeIds = nodes
            .Where(x => x.Type == WorkflowNodeType.AgentNode)
            .Select(x => x.RelateId)
            .Where(x => x != Guid.Empty)
            .ToList();

        if (agentNodeIds.Any())
        {
            var existingAgents = await _agentRepository.ListAsync(a => agentNodeIds.Contains(a.Id));
            if (existingAgents.Count != agentNodeIds.Count)
            {
                return (null, null);
            }
        }

        // Validate edges
        var edgeIds = edges.Select(x => x.EdgeId).ToList();
        if (edgeIds.Distinct().Count() != edgeIds.Count)
        {
            // Duplicate edge IDs
            return (null, null);
        }

        // Ensure all edges reference valid nodes
        foreach (var edge in edges)
        {
            if (!nodeIds.Contains(edge.SourceNodeId) || !nodeIds.Contains(edge.TargetNodeId))
            {
                return (null, null);
            }
        }

        // Normalize nodes and edges
        var normalizedNodes = nodes
            .Select(x => new WorkflowNode
            {
                WorkflowId = workflowId,
                NodeId = x.NodeId,
                Type = x.Type,
                RelateId = x.RelateId,
            })
            .ToList();

        var normalizedEdges = edges
            .Select(x => new WorkflowEdge
            {
                WorkflowId = workflowId,
                EdgeId = x.EdgeId,
                SourceNodeId = x.SourceNodeId,
                TargetNodeId = x.TargetNodeId,
                Animated = x.Animated,
            })
            .ToList();

        return (normalizedNodes, normalizedEdges);
    }
}