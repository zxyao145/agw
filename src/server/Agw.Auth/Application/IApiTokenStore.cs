using Agw.Auth.Contracts;

namespace Agw.Auth.Application;

public interface IApiTokenStore
{
    Task<IReadOnlyList<ApiTokenSummary>> ListTokensAsync(CancellationToken cancellationToken = default);

    Task<CreatedApiToken> CreateTokenAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> RevokeTokenAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);
}
