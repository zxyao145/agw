using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Pagination;
using ModelContextProtocol.Client;

namespace Agw.Agents.Definitions.Agents;

public class McpToolServerAppService
{
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<McpServer> _mcpToolServerRepository;
    private readonly IRepository<AgentMcpServerRelation> _agentMcpToolServerRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly McpToolServerDomainService _mcpToolServerDomainService;

    public McpToolServerAppService(
        IRepository<Agent> agentRepository,
        IRepository<McpServer> mcpToolServerRepository,
        IRepository<AgentMcpServerRelation> agentMcpToolServerRepository,
        IUnitOfWork unitOfWork,
        McpToolServerDomainService mcpToolServerDomainService
    )
    {
        _agentRepository = agentRepository;
        _mcpToolServerRepository = mcpToolServerRepository;
        _agentMcpToolServerRepository = agentMcpToolServerRepository;
        _unitOfWork = unitOfWork;
        _mcpToolServerDomainService = mcpToolServerDomainService;
    }

    public Task<IReadOnlyList<McpServer>> ListMcpToolServersAsync() => _mcpToolServerRepository.ListAsync();

    public Task<PagedResult<McpServer>> ListMcpToolServerPageAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default
    ) =>
        UpdatedTimePagination.ToPagedResultAsync(
            _mcpToolServerRepository.Queryable,
            server => server.Id,
            pageIndex,
            pageSize,
            cancellationToken
        );

    public Task<McpServer?> GetMcpToolServerAsync(Guid id) => _mcpToolServerRepository.GetByIdAsync(id);

    public async Task<McpServer> CreateMcpToolServerAsync(McpServer server, IEnumerable<Guid>? agentIds, string user)
    {
        _mcpToolServerDomainService.PrepareForCreate(server, user);
        await _mcpToolServerRepository.AddAsync(server);
        await SyncMcpToolServerAgentRelationsAsync(server.Id, agentIds);
        await _unitOfWork.SaveChangesAsync();
        return server;
    }

    public async Task<McpServer?> UpdateMcpToolServerAsync(Guid id, Action<McpServer> updateAction, string user)
    {
        var existing = await _mcpToolServerRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        _mcpToolServerDomainService.ApplyUpdate(existing, updateAction, user);
        _mcpToolServerRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteMcpToolServerAsync(Guid id)
    {
        var existing = await _mcpToolServerRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        _mcpToolServerRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<IReadOnlyList<McpClientTool>> ListMcpToolsAsync(
        Guid mcpToolServerId,
        CancellationToken cancellationToken = default
    )
    {
        var server = await _mcpToolServerRepository.GetByIdAsync(mcpToolServerId);
        if (server == null || !server.Enabled)
        {
            return [];
        }

        return await McpToolServerToolClient.ListToolsAsync(server, cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncMcpToolServerAgentRelationsAsync(Guid mcpToolServerId, IEnumerable<Guid>? agentIds)
    {
        var existingLinks = await _agentMcpToolServerRepository.ListAsync(x => x.McpToolServerId == mcpToolServerId);
        foreach (var link in existingLinks)
        {
            _agentMcpToolServerRepository.Remove(link);
        }

        var requestedIds = _mcpToolServerDomainService.NormalizeAgentIds(agentIds);
        if (requestedIds.Count == 0)
        {
            return;
        }

        var existingAgents = await _agentRepository.ListAsync(x => requestedIds.Contains(x.Id));
        foreach (var agentId in existingAgents.Select(x => x.Id))
        {
            await _agentMcpToolServerRepository.AddAsync(
                new AgentMcpServerRelation { AgentId = agentId, McpToolServerId = mcpToolServerId }
            );
        }
    }
}
