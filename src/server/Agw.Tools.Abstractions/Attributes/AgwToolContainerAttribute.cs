namespace Agw.Tools.Abstractions.Attributes;

/// <summary>
/// Marks a class as containing AI tools.
/// All public ordinary methods declared by this class are generated as tools unless ignored.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AgwToolContainerAttribute : Attribute
{
    public AgwToolContainerAttribute(AgwToolPermission requiredPermission)
    {
        RequiredPermission = requiredPermission;
    }

    public AgwToolPermission RequiredPermission { get; }

    /// <summary>
    /// Gets or sets the default category for all tools in this class.
    /// Individual tool categories take precedence if specified.
    /// </summary>
    public string DefaultCategory { get; set; } = "General";

    public bool AllowInPlanMode { get; set; }
}
