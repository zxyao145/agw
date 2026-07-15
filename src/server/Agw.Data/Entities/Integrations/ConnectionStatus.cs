namespace Agw.Shared.Data.Entities.Integrations;

public enum ConnectionStatus
{
    NeedsConfiguration,
    PendingAuthorization,
    Unverified,
    Ready,
    Expired,
    Invalid,
    Disabled,
    DefinitionUnavailable
}
