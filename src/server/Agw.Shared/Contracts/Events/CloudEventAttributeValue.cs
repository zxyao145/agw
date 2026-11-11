namespace Agw.Shared.Contracts.Events;

public enum CloudEventAttributeKind
{
    Boolean,
    Integer,
    String,
    Binary,
    Uri,
    UriReference,
    Timestamp,
}

public sealed record CloudEventAttributeValue(CloudEventAttributeKind Kind, string Value);
