namespace Agw.Shared.Contracts.Tools.Attributes;

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
