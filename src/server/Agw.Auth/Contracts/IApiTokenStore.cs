namespace Agw.Auth.Contracts;

public sealed record ApiTokenIdentity(string UserId);

public interface IApiTokenStore
{
    Task<IReadOnlyList<ApiTokenSummary>> ListTokensAsync(CancellationToken cancellationToken = default);

    Task<CreatedApiToken> CreateTokenAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> RevokeTokenAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ApiTokenIdentity?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);
}
