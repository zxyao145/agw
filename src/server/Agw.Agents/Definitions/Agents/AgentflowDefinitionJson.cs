using System.Text.Json;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Agents;

public static class AgentflowDefinitionJson
{
    public const string EmptyOptions = "{}";

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static T ReadOptions<T>(AgentflowNode node)
        where T : class => JsonSerializer.Deserialize<T>(node.ConfigJson, Options)!;

    public static string WriteOptions<T>(T options)
        where T : class => JsonSerializer.Serialize(options, Options);

    public static IReadOnlyList<AgentflowConditionRule> ReadConditions(AgentflowEdge edge) =>
        JsonSerializer.Deserialize<IReadOnlyList<AgentflowConditionRule>>(edge.ConditionJson, Options) ?? [];

    public static string WriteConditions(IReadOnlyList<AgentflowConditionRule> rules) =>
        JsonSerializer.Serialize(rules, Options);
}
