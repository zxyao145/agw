namespace Agw.Agents.Definitions.Domain.ValueObjects;

/// <summary>
/// 由 Application 从节点与边的配置 JSON 解析出的事实，按原始 JSON 文本索引。
/// Facts that Application parses from node and edge configuration JSON, keyed by the raw JSON text.
/// </summary>
public sealed class AgentflowConfigurationFacts
{
    public required IReadOnlyDictionary<string, bool?> OutputSummary { get; init; }
    public required IReadOnlyDictionary<string, int?> SwitchOrder { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Participants { get; init; }
    public required IReadOnlyDictionary<string, bool> Conditions { get; init; }
}
