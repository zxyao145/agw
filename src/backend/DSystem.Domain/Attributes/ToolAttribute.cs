using DSystem.Domain.Tools;

namespace DSystem.Domain.Attributes;

/// <summary>
/// Marks a method as an AI tool that can be used by agents.
/// Use DescriptionAttribute for tool description.
/// </summary>
/// <remarks>
/// This attribute is deprecated. Use <see cref="AiToolAttribute"/> instead.
/// </remarks>
[Obsolete("Use AiToolAttribute from DSystem.Domain.Tools namespace instead. This attribute will be removed in a future version.")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class ToolAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the category of the tool.
    /// </summary>
    public string Category { get; set; } = "General";
}
