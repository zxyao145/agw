using Agw.Domain.Entities;

namespace Agw.Domain.Services.Agents;

public class McpToolServerDomainService
{
    public void PrepareForCreate(McpToolServer server, string user)
    {
        ArgumentNullException.ThrowIfNull(server);

        NormalizeCollections(server);
        server.Id = server.Id == Guid.Empty ? Guid.NewGuid() : server.Id;
        server.CreateBy = user;
        server.CreateTime = DateTime.UtcNow;
    }

    public void ApplyUpdate(McpToolServer existing, Action<McpToolServer> updateAction, string user)
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

    private static void NormalizeCollections(McpToolServer server)
    {
        server.Arguments ??= [];
        server.EnvironmentVariables ??= new Dictionary<string, string>();
        server.Headers ??= new Dictionary<string, string>();
    }
}
