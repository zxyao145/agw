namespace Agw.Tools.Contracts;

/// <summary>Describes a selectable Tool or ToolBlock without execution implementation details.</summary>
public sealed record ToolLiteInfo
{
    public ToolCatalogItemKind Kind { get; init; } = ToolCatalogItemKind.Tool;

    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    public required string Category { get; init; }

    public IReadOnlyList<string> MemberToolNames { get; init; } = [];

    public ToolScope Scopes { get; init; } = ToolScope.Agent | ToolScope.Project;

    public bool RequiresWorkspace { get; init; }

    /// <summary>The standalone Tool's permission; null for a ToolBlock.</summary>
    public AgwToolPermission? RequiredPermission { get; init; }

    /// <summary>Whether this Tool or any ToolBlock member may require execution approval.</summary>
    public bool RequiresConfirmation { get; init; }
}
