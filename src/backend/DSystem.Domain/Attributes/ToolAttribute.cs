using System;

namespace DSystem.Domain.Attributes;

/// <summary>
/// Marks a method as an AI tool that can be used by agents.
/// Use DescriptionAttribute for tool description.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class ToolAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the category of the tool.
    /// </summary>
    public string Category { get; set; } = "General";
}
