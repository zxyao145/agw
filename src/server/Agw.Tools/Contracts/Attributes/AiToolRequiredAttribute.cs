namespace Agw.Tools.Contracts.Attributes;

/// <summary>
/// Marks a parameter as required for the tool execution.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class AiToolRequiredAttribute : Attribute { }
