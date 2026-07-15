namespace Agw.Integrations.Domain.Plugins;

public sealed class FormFieldDefinition
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required FormFieldType Type { get; init; }

    public bool IsRequired { get; init; }

    public string? Description { get; init; }
}

public enum FormFieldType
{
    Text,
    Secret,
    Url,
}
