namespace Agw.Shared.Contracts.Tools.Attributes;

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
