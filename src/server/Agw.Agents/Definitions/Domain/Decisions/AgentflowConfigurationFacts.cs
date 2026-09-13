namespace Agw.Agents.Definitions.Domain.Decisions;

public sealed class AgentflowConfigurationFacts
{
    public required IReadOnlyDictionary<string, bool?> OutputSummary { get; init; }
    public required IReadOnlyDictionary<string, int?> SwitchOrder { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Participants { get; init; }
    public required IReadOnlyDictionary<string, bool> Conditions { get; init; }
}
