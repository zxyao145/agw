using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;

namespace DSystem.Domain.Services;

public class McpToolServerDomainService
{
    private readonly IRepository<McpToolServer> _repository;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<AgentMcpToolServer> _agentMcpRepository;
    private readonly IUnitOfWork _unitOfWork;

    public McpToolServerDomainService(
        IRepository<McpToolServer> repository,
        IRepository<Agent> agentRepository,
        IRepository<AgentMcpToolServer> agentMcpRepository,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _agentRepository = agentRepository;
        _agentMcpRepository = agentMcpRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<McpToolServer> CreateAsync(McpToolServer server, IEnumerable<Guid>? agentIds, string user)
    {
        server.Id = server.Id == Guid.Empty ? Guid.NewGuid() : server.Id;
        server.CreateBy = user;
        server.CreateTime = DateTime.UtcNow;
        await _repository.AddAsync(server);
        await SyncAgentRelationsAsync(server.Id, agentIds);
        await _unitOfWork.SaveChangesAsync();
        return server;
    }

    public async Task<McpToolServer?> UpdateAsync(Guid id, Action<McpToolServer> updateAction, IEnumerable<Guid>? agentIds, string user)
    {
        var existing = await _repository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        updateAction(existing);
        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;
        _repository.Update(existing);
        await SyncAgentRelationsAsync(id, agentIds);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await _repository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        _repository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public Task<IReadOnlyList<McpToolServer>> ListAsync() => _repository.ListAsync();

    public Task<McpToolServer?> GetAsync(Guid id) => _repository.GetByIdAsync(id);

    private async Task SyncAgentRelationsAsync(Guid mcpToolServerId, IEnumerable<Guid>? agentIds)
    {
        var existingLinks = await _agentMcpRepository.ListAsync(x => x.McpToolServerId == mcpToolServerId);
        foreach (var link in existingLinks)
        {
            _agentMcpRepository.Remove(link);
        }

        var requestedIds = (agentIds ?? [])
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();
        if (requestedIds.Count == 0)
        {
            return;
        }

        var existingAgents = await _agentRepository.ListAsync(x => requestedIds.Contains(x.Id));
        foreach (var agentId in existingAgents.Select(x => x.Id))
        {
            await _agentMcpRepository.AddAsync(new AgentMcpToolServer
            {
                AgentId = agentId,
                McpToolServerId = mcpToolServerId
            });
        }
    }
}
