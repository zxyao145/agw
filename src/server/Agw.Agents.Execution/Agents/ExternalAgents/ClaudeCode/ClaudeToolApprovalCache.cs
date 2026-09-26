using System.Text.Json.Nodes;

namespace Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;

internal sealed class ClaudeToolApprovalCache
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Grants> _scopes = new(StringComparer.Ordinal);

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
