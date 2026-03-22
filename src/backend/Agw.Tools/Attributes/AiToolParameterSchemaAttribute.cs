namespace Agw.Tools.Attributes;

/// <summary>
/// Provides JSON schema information for a parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class AiToolParameterSchemaAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the JSON schema type (e.g., "string", "number", "boolean", "array", "object").
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the format hint (e.g., "date-time", "email", "uri").
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    /// Gets or sets the allowed enum values as a comma-separated string.
    /// </summary>
    public string? EnumValues { get; set; }

    /// <summary>
    /// Gets or sets the minimum value for numeric parameters.
    /// </summary>
    public double? Minimum { get; set; }

    /// <summary>
    /// Gets or sets the maximum value for numeric parameters.
    /// </summary>
    public double? Maximum { get; set; }

    /// <summary>
    /// Gets or sets the minimum length for string parameters.
    /// </summary>
    public int? MinLength { get; set; }

    /// <summary>
    /// Gets or sets the maximum length for string parameters.
    /// </summary>
    public int? MaxLength { get; set; }

    /// <summary>
    /// Gets or sets the regex pattern for string validation.
    /// </summary>
    public string? Pattern { get; set; }
}
