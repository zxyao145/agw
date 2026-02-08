using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;
using DSystem.Shared.Enums;
using System.Linq.Expressions;

namespace DSystem.Domain.Services;

public class AgentflowDomainService
{
    private readonly IRepository<Agentflow> _agentflowRepository;
    private readonly IRepository<AgentflowNode> _agentflowNodeRepository;
    private readonly IRepository<AgentflowEdge> _agentflowEdgeRepository;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IUnitOfWork _unitOfWork;

    public AgentflowDomainService(
        IRepository<Agentflow> agentflowRepository,
        IRepository<AgentflowNode> agentflowNodeRepository,
        IRepository<AgentflowEdge> agentflowEdgeRepository,
        IRepository<Agent> agentRepository,
        IUnitOfWork unitOfWork)
    {
        _agentflowRepository = agentflowRepository;
        _agentflowNodeRepository = agentflowNodeRepository;
        _agentflowEdgeRepository = agentflowEdgeRepository;
        _agentRepository = agentRepository;
        _unitOfWork = unitOfWork;
    }

    public Task<IReadOnlyList<Agentflow>> ListAsync(Expression<Func<Agentflow, bool>>? predicate = null) =>
        _agentflowRepository.ListAsync(predicate);

    public Task<Agentflow?> GetAsync(Guid id) => _agentflowRepository.GetByIdAsync(id);

    public Task<IReadOnlyList<AgentflowNode>> ListNodesAsync(Guid agentflowId) =>
        _agentflowNodeRepository.ListAsync(x => x.AgentflowId == agentflowId);

    public Task<IReadOnlyList<AgentflowEdge>> ListEdgesAsync(Guid agentflowId) =>
        _agentflowEdgeRepository.ListAsync(x => x.AgentflowId == agentflowId);

    public async Task<Agentflow?> CreateAsync(
        Agentflow agentflow,
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        string user)
    {
        if (string.IsNullOrWhiteSpace(agentflow.Name))
        {
            return null;
        }

        agentflow.Id = agentflow.Id == Guid.Empty ? Guid.NewGuid() : agentflow.Id;
        agentflow.CreateBy = user;
        agentflow.CreateTime = DateTime.UtcNow;

        var (normalizedNodes, normalizedEdges) = await ValidateAndNormalizeGraphAsync(
            agentflow.Pattern, nodes, edges, agentflow.Id);
        if (normalizedNodes == null || normalizedEdges == null)
        {
            return null;
        }

        await _agentflowRepository.AddAsync(agentflow);

        foreach (var node in normalizedNodes)
        {
            node.CreateBy = user;
            node.CreateTime = agentflow.CreateTime;
            await _agentflowNodeRepository.AddAsync(node);
        }

        foreach (var edge in normalizedEdges)
        {
            edge.CreateBy = user;
            edge.CreateTime = agentflow.CreateTime;
            await _agentflowEdgeRepository.AddAsync(edge);
        }

        await _unitOfWork.SaveChangesAsync();
        return agentflow;
    }

    public async Task<Agentflow?> UpdateAsync(
        Guid id,
        Action<Agentflow> updateAction,
        IReadOnlyList<AgentflowNode>? nodes,
        IReadOnlyList<AgentflowEdge>? edges,
        string user)
    {
        var existing = await _agentflowRepository.GetByIdAsync(id);
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

        _agentflowRepository.Update(existing);

        if (nodes != null && edges != null)
        {
            var (normalizedNodes, normalizedEdges) = await ValidateAndNormalizeGraphAsync(
                existing.Pattern, nodes, edges, existing.Id);
            if (normalizedNodes == null || normalizedEdges == null)
            {
                return null;
            }

            // Remove existing nodes and edges
            var currentNodes = await _agentflowNodeRepository.ListAsync(x => x.AgentflowId == existing.Id);
            foreach (var item in currentNodes)
            {
                _agentflowNodeRepository.Remove(item);
            }

            var currentEdges = await _agentflowEdgeRepository.ListAsync(x => x.AgentflowId == existing.Id);
            foreach (var item in currentEdges)
            {
                _agentflowEdgeRepository.Remove(item);
            }

            // Add new nodes and edges
            foreach (var node in normalizedNodes)
            {
                node.CreateBy ??= existing.CreateBy;
                node.CreateTime = existing.CreateTime == default ? DateTime.UtcNow : existing.CreateTime;
                node.UpdateBy = user;
                node.UpdateTime = DateTime.UtcNow;
                await _agentflowNodeRepository.AddAsync(node);
            }

            foreach (var edge in normalizedEdges)
            {
                edge.CreateBy ??= existing.CreateBy;
                edge.CreateTime = existing.CreateTime == default ? DateTime.UtcNow : existing.CreateTime;
                edge.UpdateBy = user;
                edge.UpdateTime = DateTime.UtcNow;
                await _agentflowEdgeRepository.AddAsync(edge);
            }
        }

        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await _agentflowRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        _agentflowRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    private async Task<(IReadOnlyList<AgentflowNode>?, IReadOnlyList<AgentflowEdge>?)> ValidateAndNormalizeGraphAsync(
        AgentflowOrchestrationPattern pattern,
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        Guid agentflowId)
    {
        if (nodes == null || edges == null)
        {
            return (Array.Empty<AgentflowNode>(), Array.Empty<AgentflowEdge>());
        }

        if (pattern == AgentflowOrchestrationPattern.Sequential && nodes.Count == 0)
        {
            return (null, null);
        }

        // Validate nodes
        var nodeIds = nodes.Select(x => x.NodeId).ToList();
        if (nodeIds.Count == 0)
        {
            return (Array.Empty<AgentflowNode>(), Array.Empty<AgentflowEdge>());
        }

        // Ensure unique node IDs
        if (nodeIds.Distinct().Count() != nodeIds.Count)
        {
            return (null, null);
        }

        // Validate that all AgentNode references exist
        var agentNodeIds = nodes
            .Where(x => x.Type == AgentflowNodeType.AgentNode)
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
            .Select(x => new AgentflowNode
            {
                AgentflowId = agentflowId,
                NodeId = x.NodeId,
                Type = x.Type,
                RelateId = x.RelateId,
            })
            .ToList();

        var normalizedEdges = edges
            .Select(x => new AgentflowEdge
            {
                AgentflowId = agentflowId,
                EdgeId = x.EdgeId,
                SourceNodeId = x.SourceNodeId,
                TargetNodeId = x.TargetNodeId,
                Animated = x.Animated,
            })
            .ToList();

        return (normalizedNodes, normalizedEdges);
    }
}