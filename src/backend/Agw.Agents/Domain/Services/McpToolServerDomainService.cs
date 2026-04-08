using Agw.Agents.Domain.Entities;

namespace Agw.Agents.Domain.Services;

public class McpToolServerDomainService
{
    public void PrepareForCreate(McpServer server, string user)
    {
        ArgumentNullException.ThrowIfNull(server);

        NormalizeCollections(server);
        server.Id = server.Id == Guid.Empty ? Guid.NewGuid() : server.Id;
        server.CreateBy = user;
        server.CreateTime = DateTime.UtcNow;
    }

    public void ApplyUpdate(McpServer existing, Action<McpServer> updateAction, string user)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(updateAction);

        updateAction(existing);
        NormalizeCollections(existing);
        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;
    }

    public IReadOnlyList<Guid> NormalizeAgentIds(IEnumerable<Guid>? agentIds)
    {
        return (agentIds ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
    }

    private static void NormalizeCollections(McpServer server)
    {
        server.Arguments ??= [];
        server.EnvironmentVariables ??= new Dictionary<string, string>();
        server.Headers ??= new Dictionary<string, string>();
    }
}
