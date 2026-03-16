namespace DSystem.Domain.Tools;

/// <summary>
/// Marks a method as an AI tool that can be used by agents.
/// This attribute provides metadata for tool discovery and registration.
/// Use System.ComponentModel.DescriptionAttribute for detailed tool/parameter descriptions.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AiToolAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AiToolAttribute"/> class.
    /// </summary>
    public AiToolAttribute()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AiToolAttribute"/> class with a name.
    /// </summary>
    /// <param name="name">The name of the tool. If not specified, the method name is used.</param>
    public AiToolAttribute(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Gets or sets the name of the tool.
    /// If not specified, the method name is used.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the category of the tool for grouping purposes.
    /// </summary>
    public string Category { get; set; } = "General";

    /// <summary>
    /// Gets or sets whether the tool requires confirmation before execution.
    /// </summary>
    public bool RequiresConfirmation { get; set; }

    /// <summary>
    /// Gets or sets the execution timeout in milliseconds.
    /// Default is 30000 (30 seconds). Set to 0 for no timeout.
    /// </summary>
    public int TimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Gets or sets whether exceptions should be returned as tool results
    /// instead of being thrown.
    /// </summary>
    public bool ReturnExceptionsAsResults { get; set; } = true;
}

/// <summary>
/// Marks a class as containing AI tools.
/// All public methods with AiToolAttribute in this class will be registered.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AiToolContainerAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the default category for all tools in this class.
    /// Individual tool categories take precedence if specified.
    /// </summary>
    public string DefaultCategory { get; set; } = "General";
}

/// <summary>
/// Marks a parameter as required for the tool execution.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class AiToolRequiredAttribute : Attribute
{
}

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
