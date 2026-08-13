using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Pagination;

namespace Agw.Agents.Definitions.Agents;

public class AgentflowAppService
{
    private readonly IRepository<Agentflow> _agentflowRepository;
    private readonly IRepository<AgentflowNode> _agentflowNodeRepository;
    private readonly IRepository<AgentflowEdge> _agentflowEdgeRepository;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<ModelProviderRelation> _modelProviderRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AgentflowDomainService _agentflowDomainService;
    private readonly TimeProvider _timeProvider;

    public AgentflowAppService(
        IRepository<Agentflow> agentflowRepository,
        IRepository<AgentflowNode> agentflowNodeRepository,
        IRepository<AgentflowEdge> agentflowEdgeRepository,
        IRepository<Agent> agentRepository,
        IRepository<ModelProviderRelation> modelProviderRepository,
        IUnitOfWork unitOfWork,
        AgentflowDomainService agentflowDomainService,
        TimeProvider timeProvider)
    {
        _agentflowRepository = agentflowRepository;
        _agentflowNodeRepository = agentflowNodeRepository;
        _agentflowEdgeRepository = agentflowEdgeRepository;
        _agentRepository = agentRepository;
        _modelProviderRepository = modelProviderRepository;
        _unitOfWork = unitOfWork;
        _agentflowDomainService = agentflowDomainService;
        _timeProvider = timeProvider;
    }

    public Task<IReadOnlyList<Agentflow>> ListAsync() => _agentflowRepository.ListAsync();

    public Task<PagedResult<Agentflow>> ListPageAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        UpdatedTimePagination.ToPagedResultAsync(
            _agentflowRepository.Queryable,
            agentflow => agentflow.Id,
            pageIndex,
            pageSize,
            cancellationToken);

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
        if (!_agentflowDomainService.TryPrepareForCreate(agentflow, user))
        {
            return null;
        }

        var existingAgents = await ListExistingAgentsAsync(nodes);
        var existingModelProviderIds = await ListExistingModelProviderIdsAsync(agentflow.SummaryModelProviderId);
        var (normalizedNodes, normalizedEdges) = _agentflowDomainService.ValidateAndNormalizeGraph(
            nodes,
            edges,
            agentflow.Id,
            existingAgents.Keys.ToList(),
            agentflow.SummaryModelProviderId,
            existingModelProviderIds,
            existingAgents);
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

        if (!_agentflowDomainService.TryApplyUpdate(existing, updateAction, user))
        {
            return null;
        }

        if (nodes != null && edges != null)
        {
            var existingAgents = await ListExistingAgentsAsync(nodes);
            var existingModelProviderIds = await ListExistingModelProviderIdsAsync(existing.SummaryModelProviderId);
            var (normalizedNodes, normalizedEdges) = _agentflowDomainService.ValidateAndNormalizeGraph(
                nodes,
                edges,
                existing.Id,
                existingAgents.Keys.ToList(),
                existing.SummaryModelProviderId,
                existingModelProviderIds,
                existingAgents);
            if (normalizedNodes == null || normalizedEdges == null)
            {
                return null;
            }

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

            var now = _timeProvider.GetUtcNow();
            foreach (var node in normalizedNodes)
            {
                node.CreateBy ??= existing.CreateBy;
                node.CreateTime = existing.CreateTime == default ? now : existing.CreateTime;
                node.UpdateBy = user;
                node.UpdateTime = now;
                await _agentflowNodeRepository.AddAsync(node);
            }

            foreach (var edge in normalizedEdges)
            {
                edge.CreateBy ??= existing.CreateBy;
                edge.CreateTime = existing.CreateTime == default ? now : existing.CreateTime;
                edge.UpdateBy = user;
                edge.UpdateTime = now;
                await _agentflowEdgeRepository.AddAsync(edge);
            }
        }

        _agentflowRepository.Update(existing);
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

        var currentEdges = await _agentflowEdgeRepository.ListAsync(x => x.AgentflowId == existing.Id);
        foreach (var edge in currentEdges)
        {
            _agentflowEdgeRepository.Remove(edge);
        }

        var currentNodes = await _agentflowNodeRepository.ListAsync(x => x.AgentflowId == existing.Id);
        foreach (var node in currentNodes)
        {
            _agentflowNodeRepository.Remove(node);
        }

        _agentflowRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ListExistingAgentsAsync(
        IReadOnlyList<AgentflowNode> nodes)
    {
        var agentIds = nodes
            .Where(x => x.Kind == AgentflowNodeKind.Agent)
            .Select(x => x.RelateId)
            .Where(x => x is not null && x.Value != Guid.Empty)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
        if (agentIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var existingAgents = await _agentRepository.ListAsync(x => agentIds.Contains(x.Id));
        return existingAgents.ToDictionary(x => x.Id, x => x.Name);
    }

    private async Task<IReadOnlyCollection<Guid>> ListExistingModelProviderIdsAsync(Guid? modelProviderId)
    {
        if (!modelProviderId.HasValue)
        {
            return Array.Empty<Guid>();
        }

        var existing = await _modelProviderRepository.GetByIdAsync(modelProviderId.Value);
        return existing == null ? Array.Empty<Guid>() : [existing.Id];
    }
}
