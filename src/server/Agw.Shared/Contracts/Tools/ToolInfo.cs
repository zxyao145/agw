using System.Text.Json.Serialization;

namespace Agw.Shared.Contracts.Tools;

[JsonConverter(typeof(JsonStringEnumConverter<ToolCatalogItemKind>))]
public enum ToolCatalogItemKind
{
    [JsonStringEnumMemberName("tool")]
    Tool,

    [JsonStringEnumMemberName("toolBlock")]
    ToolBlock,
}

[Flags]
public enum ToolScope
{
    None = 0,
    Agent = 1,
    Project = 2,
}

/// <summary>
/// Represents a selectable Tool or Tool Block in the Tools catalog.
/// </summary>
public record ToolInfo
{
    public ToolCatalogItemKind Kind { get; init; } = ToolCatalogItemKind.Tool;

    /// <summary>
    /// Gets the name of the tool (method name or custom name).
    /// </summary>
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the description of the tool.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the category of the tool for grouping purposes.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Gets the full type name of the class containing the tool.
    /// </summary>
    public required string TypeName { get; init; }

    public IReadOnlyList<string> MemberToolNames { get; init; } = [];

    public ToolScope Scopes { get; init; } = ToolScope.Agent | ToolScope.Project;

    public bool RequiresWorkspace { get; init; }

    /// <summary>
    /// Gets the parameters of the tool.
    /// </summary>
    public required IReadOnlyList<ToolParameterInfo> Parameters { get; init; }

    /// <summary>
    /// Gets whether this is an asynchronous tool.
    /// </summary>
    public bool IsAsync { get; init; }

    /// <summary>
    /// Gets whether this tool requires user confirmation before execution.
    /// </summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>
    /// Gets the execution timeout in milliseconds.
    /// </summary>
    public int TimeoutMs { get; init; } = 30000;
}

/// <summary>
/// Represents information about a parameter of a tool.
/// </summary>
public record ToolParameterInfo
{
    /// <summary>
    /// Gets the name of the parameter.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the type of the parameter.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Gets the description of the parameter.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets whether the parameter is optional.
    /// </summary>
    public bool IsOptional { get; init; }

    /// <summary>
    /// Gets the default value for the parameter if it has one.
    /// </summary>
    public object? DefaultValue { get; init; }

    /// <summary>
    /// Gets the JSON schema type hint (e.g., "string", "number", "boolean").
    /// </summary>
    public string? SchemaType { get; init; }

    /// <summary>
    /// Gets the format hint (e.g., "date-time", "email", "uri").
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// Gets allowed enum values if applicable.
    /// </summary>
    public IReadOnlyList<string>? EnumValues { get; init; }
}
