namespace Agw.Agents.ExternalAgents.Acp;

public sealed record AcpAgentSettings(
    AcpRegistryReference? Registry,
    string? Command,
    IReadOnlyList<string>? Args,
    string? AuthMethodId,
    string? ModeId,
    IReadOnlyDictionary<string, AcpConfigOptionValue>? ConfigOptions
);

public sealed record AcpRegistryReference(string AgentId, string? Version);

public sealed record AcpConfigOptionValue(string? Text, bool? Boolean);
