using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Definitions.Domain;

public class McpToolServerDomainService
{
    private readonly TimeProvider _timeProvider;

    public McpToolServerDomainService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public void PrepareForCreate(McpServer server, string user)
    {
        ArgumentNullException.ThrowIfNull(server);

        NormalizeCollections(server);
        server.Id = server.Id == Guid.Empty ? Guid.NewGuid() : server.Id;
        server.CreateBy = user;
        server.CreateTime = _timeProvider.GetUtcNow();
    }

    public void ApplyUpdate(McpServer existing, Action<McpServer> updateAction, string user)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(updateAction);

        updateAction(existing);
        NormalizeCollections(existing);
        existing.UpdateBy = user;
        existing.UpdateTime = _timeProvider.GetUtcNow();
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
