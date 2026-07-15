namespace Agw.Integrations.Domain.Plugins;

public sealed class ConnectorDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<AuthSchemeDefinition> AuthSchemes { get; init; } = [];

    public IReadOnlyList<CapabilitySourceDefinition> CapabilitySources { get; init; } = [];
}
