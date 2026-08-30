namespace Agw.Shared.Contracts;

/// <summary>
/// Cross-module view of the authenticated user. The Auth module owns the
/// implementation; business modules depend only on this stable contract.
/// </summary>
public interface ICurrentUser
{
    string? UserId { get; }

    bool IsAuthenticated { get; }

    string RequiredUserId { get; }
}
