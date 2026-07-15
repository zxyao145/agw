namespace Agw.Integrations.Domain.Plugins;

public sealed class PluginDefinition
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// A plugin can expose multiple service or protocol connectors.
    /// </summary>
    public IReadOnlyList<ConnectorDefinition> Connectors { get; init; } = [];

    public IReadOnlyList<PluginSkillDefinition> Skills { get; init; } = [];
}
