namespace Agw.Tools.Abstractions.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ExcludeToolFromListAttribute : Attribute;
