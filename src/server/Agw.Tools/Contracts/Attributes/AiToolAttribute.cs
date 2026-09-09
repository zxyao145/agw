namespace Agw.Tools.Contracts.Attributes;

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
    public AiToolAttribute(AgwToolPermission requiredPermission)
    {
        RequiredPermission = requiredPermission;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AiToolAttribute"/> class with a name.
    /// </summary>
    /// <param name="name">The name of the tool. If not specified, the method name is used.</param>
    /// <param name="requiredPermission">The permission required to invoke the tool.</param>
    public AiToolAttribute(string name, AgwToolPermission requiredPermission)
    {
        Name = name;
        RequiredPermission = requiredPermission;
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

    public AgwToolPermission RequiredPermission { get; }

    /// <summary>
    /// Gets or sets whether the tool is trusted for use in Plan mode.
    /// </summary>
    public bool AllowInPlanMode { get; set; }

    /// <summary>
    /// Gets or sets the execution timeout in milliseconds.
    /// Default is 30000 (30 seconds). Set to 0 for no timeout.
    /// </summary>
    public int TimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Gets or sets the legacy exception-result preference.
    /// The Agent execution pipeline always returns Tool exceptions as results;
    /// this property is retained for compatibility and cannot disable that policy.
    /// </summary>
    public bool ReturnExceptionsAsResults { get; set; } = true;
}
