using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Definitions.Domain.Behaviors;

public sealed class McpServerBehavior
{
    private readonly McpServer _server;

    public McpServerBehavior(McpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
    }

    public void NormalizeCollections()
    {
        _server.Arguments ??= [];
        _server.EnvironmentVariables ??= new Dictionary<string, string>();
        _server.Headers ??= new Dictionary<string, string>();
    }
}
