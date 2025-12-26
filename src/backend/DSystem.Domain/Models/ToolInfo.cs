namespace DSystem.Domain.Models;

/// <summary>
/// Represents a tool that can be used by an agent.
/// </summary>
public record ToolInfo
{
    /// <summary>
    /// Gets the name of the tool (method name).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the description of the tool.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the category of the tool.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Gets the full type name of the class containing the tool.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Gets the parameters of the tool.
    /// </summary>
    public required IReadOnlyList<ToolParameterInfo> Parameters { get; init; }
}

/// <summary>
/// Represents a parameter of a tool.
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
}
