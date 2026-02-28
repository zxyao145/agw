using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace DSystem.Domain.Services;

public class McpToolServerDomainService
{
    private readonly IRepository<McpToolServer> _repository;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<AgentMcpToolServer> _agentMcpRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<McpToolServerDomainService> _logger;

    public McpToolServerDomainService(
        IRepository<McpToolServer> repository,
        IRepository<Agent> agentRepository,
        IRepository<AgentMcpToolServer> agentMcpRepository,
        IUnitOfWork unitOfWork,
        ILogger<McpToolServerDomainService> logger)
    {
        _repository = repository;
        _agentRepository = agentRepository;
        _agentMcpRepository = agentMcpRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
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

    public async Task<IReadOnlyList<McpClientTool>> ListToolsByAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var links = await _agentMcpRepository.ListAsync(x => x.AgentId == agentId);
        var serverIds = links
            .Select(x => x.McpToolServerId)
            .Distinct()
            .ToList();

        if (serverIds.Count == 0)
        {
            return [];
        }

        var servers = await _repository.ListAsync(x => x.Enabled && serverIds.Contains(x.Id));
        var tools = new List<McpClientTool>();

        foreach (var server in servers)
        {
            try
            {
                var serverTools = await ListToolsAsync(server, cancellationToken).ConfigureAwait(false);
                if (serverTools.Count > 0)
                {
                    tools.AddRange(serverTools);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list MCP tools from server {ServerId}", server.Id);
            }
        }

        return tools;
    }


    public async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(Guid mcpToolServerId, CancellationToken cancellationToken = default)
    {
        var server = await _repository.GetByIdAsync(mcpToolServerId);
        if (server == null || !server.Enabled)
        {
            return [];
        }

        return await ListToolsAsync(server, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(
        McpToolServer server,
        CancellationToken cancellationToken = default)
    {
        var transport = CreateTransport(server);
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return tools.AsReadOnly();
    }

    private static IClientTransport CreateTransport(McpToolServer server)
    {
        return server.TransportType.ToLowerInvariant() switch
        {
            "stdio" => CreateStdioTransport(server),
            "http" or "sse" => CreateHttpTransport(server),
            _ => throw new NotSupportedException($"Transport type '{server.TransportType}' is not supported")
        };
    }

    private static StdioClientTransport CreateStdioTransport(McpToolServer server)
    {
        if (string.IsNullOrWhiteSpace(server.Command))
        {
            throw new InvalidOperationException($"MCP server '{server.Id}' uses stdio transport but has no command configured");
        }

        var options = new StdioClientTransportOptions
        {
            Name = server.Name,
            Command = server.Command,
            Arguments = [.. server.Arguments],
        };

        if (server.EnvironmentVariables.Count > 0)
        {
            options.EnvironmentVariables = new Dictionary<string, string?>(
                server.EnvironmentVariables.Select(kvp => new KeyValuePair<string, string?>(kvp.Key, kvp.Value)));
        }

        if (!string.IsNullOrWhiteSpace(server.WorkingDirectory))
        {
            options.WorkingDirectory = server.WorkingDirectory;
        }

        return new StdioClientTransport(options);
    }

    private static HttpClientTransport CreateHttpTransport(McpToolServer server)
    {
        if (string.IsNullOrWhiteSpace(server.Url))
        {
            throw new InvalidOperationException($"MCP server '{server.Id}' uses HTTP/SSE transport but has no URL configured");
        }

        var options = new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url),
        };

        if (server.Headers is { Count: > 0 })
        {
            options.AdditionalHeaders = new Dictionary<string, string>(server.Headers);
        }

        return new HttpClientTransport(options);
    }

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
