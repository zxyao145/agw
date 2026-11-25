namespace Agw.Agents.Definitions.Contracts;

public sealed record AcpRegistryResponse(
    Uri SourceUrl,
    DateTimeOffset FetchedAt,
    IReadOnlyList<AcpRegistryAgentResponse> Agents
);

public sealed record AcpRegistryAgentResponse(
    string Id,
    string Name,
    string Version,
    string Description,
    string? Repository,
    string? Website,
    string? License,
    IReadOnlyList<string> Distributions
);
