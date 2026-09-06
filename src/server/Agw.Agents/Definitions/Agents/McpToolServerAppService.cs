using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Auth.Contracts;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Pagination;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;

namespace Agw.Agents.Definitions.Agents;

public class McpToolServerAppService
{
    private readonly IAgentsDbContext _dbContext;
    private readonly IUserInfoService _userInfoService;

    public McpToolServerAppService(IAgentsDbContext dbContext, IUserInfoService userInfoService)
    {
        _dbContext = dbContext;
        _userInfoService = userInfoService;
    }

    public async Task<IReadOnlyList<McpServer>> ListMcpToolServersAsync()
    {
        var ownerUserId = ResolveOwnerUserId();
        return await _dbContext
            .McpToolServers.AsNoTracking()
            .Where(server => server.CreateBy == ownerUserId)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public Task<PagedResult<McpServer>> ListMcpToolServerPageAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default
    ) =>
        UpdatedTimePagination.ToPagedResultAsync(
            _dbContext.McpToolServers.AsNoTracking().Where(server => server.CreateBy == ResolveOwnerUserId()),
            server => server.Id,
            pageIndex,
            pageSize,
            cancellationToken
        );

    public Task<McpServer?> GetMcpToolServerAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        return _dbContext
            .McpToolServers.AsNoTracking()
            .FirstOrDefaultAsync(server => server.Id == id && server.CreateBy == ownerUserId);
    }

    public async Task<McpServer> CreateMcpToolServerAsync(McpServer server, IEnumerable<Guid>? agentIds, string user)
    {
        var ownerUserId = ResolveOwnerUserId();
        new McpServerBehavior(server).NormalizeCollections();
        server.Id = server.Id == Guid.Empty ? Guid.CreateVersion7() : server.Id;
        await _dbContext.McpToolServers.AddAsync(server);
        await SyncMcpToolServerAgentRelationsAsync(server.Id, agentIds, ownerUserId);
        await _dbContext.SaveChangesAsync();
        return server;
    }

    public async Task<McpServer?> UpdateMcpToolServerAsync(Guid id, Action<McpServer> updateAction, string user)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext.McpToolServers.FirstOrDefaultAsync(server =>
            server.Id == id && server.CreateBy == ownerUserId
        );
        if (existing == null)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(updateAction);
        updateAction(existing);
        new McpServerBehavior(existing).NormalizeCollections();
        _dbContext.McpToolServers.Entry(existing).Property(server => server.Name).IsModified = true;
        await _dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteMcpToolServerAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext.McpToolServers.FirstOrDefaultAsync(server =>
            server.Id == id && server.CreateBy == ownerUserId
        );
        if (existing == null)
        {
            return false;
        }

        _dbContext.McpToolServers.Remove(existing);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<IReadOnlyList<McpClientTool>> ListMcpToolsAsync(
        Guid mcpToolServerId,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = ResolveOwnerUserId();
        var server = await _dbContext
            .McpToolServers.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == mcpToolServerId && item.CreateBy == ownerUserId);
        if (server == null || !server.Enabled)
        {
            return [];
        }

        return await McpToolServerToolClient.ListToolsAsync(server, cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncMcpToolServerAgentRelationsAsync(
        Guid mcpToolServerId,
        IEnumerable<Guid>? agentIds,
        string user
    )
    {
        var requestedIds = (agentIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        var existingLinks = await _dbContext
            .AgentMcpToolServers.Where(link => link.McpToolServerId == mcpToolServerId)
            .ToListAsync();
        if (requestedIds.Count == 0)
        {
            _dbContext.AgentMcpToolServers.RemoveRange(existingLinks);
            return;
        }

        var existingAgents = await _dbContext
            .Agents.AsNoTracking()
            .Where(agent => requestedIds.Contains(agent.Id) && agent.CreateBy == user)
            .Select(agent => agent.Id)
            .ToListAsync();
        if (existingAgents.Count != requestedIds.Count)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        var linksToRemove = existingLinks.Where(link => !requestedIds.Contains(link.AgentId)).ToArray();
        _dbContext.AgentMcpToolServers.RemoveRange(linksToRemove);
        var existingAgentIds = existingLinks.Select(link => link.AgentId).ToHashSet();
        foreach (var agentId in existingAgents.Where(id => !existingAgentIds.Contains(id)))
        {
            await _dbContext.AgentMcpToolServers.AddAsync(
                new AgentMcpServerRelation { AgentId = agentId, McpToolServerId = mcpToolServerId }
            );
        }
    }

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;
}
