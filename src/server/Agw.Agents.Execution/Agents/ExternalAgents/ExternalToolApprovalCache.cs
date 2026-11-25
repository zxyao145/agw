using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Agents.Execution.HumanInteraction.Application;

namespace Agw.Agents.Execution.Agents.ExternalAgents;

internal sealed class ExternalToolApprovalCache
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Grants> _scopes = new(StringComparer.Ordinal);

    public static (InteractionSource Source, string? Scope, long Version) BindScope(
        InteractionSource? interactionSource,
        AgentExecutionContext? context,
        Guid agentId,
        string? workspace
    )
    {
        var source = interactionSource ?? new InteractionSource { NodeId = InteractionIdentity.StandaloneNodeId };
        if (context is null)
            return (source, null, 0);
        var scope = JsonSerializer.Serialize(
            new
            {
                context.UserId,
                context.ProjectId,
                context.ProjectConversationId,
                context.Generation,
                AgentId = agentId,
                source.NodeId,
                Workspace = workspace,
            }
        );
        return (source, scope, context.PermissionVersion);
    }

    public bool Contains(string scope, long version, string tool, JsonNode? arguments)
    {
        lock (_sync)
            return Get(scope, version).Values.Any(g => g.Tool == tool && JsonNode.DeepEquals(g.Arguments, arguments));
    }

    public void Add(string scope, long version, string tool, JsonNode? arguments)
    {
        lock (_sync)
            Get(scope, version).Values.Add((tool, arguments?.DeepClone()));
    }

    private Grants Get(string scope, long version)
    {
        if (!_scopes.TryGetValue(scope, out var grants) || grants.Version != version)
            _scopes[scope] = grants = new Grants(version);
        return grants;
    }

    private sealed class Grants
    {
        public Grants(long version)
        {
            Version = version;
        }

        public long Version { get; }
        public List<(string Tool, JsonNode? Arguments)> Values { get; } = [];
    }
}
