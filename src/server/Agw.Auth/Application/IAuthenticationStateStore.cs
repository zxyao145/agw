using Agw.Auth.Contracts;

namespace Agw.Auth.Application;

public interface IAuthenticationStateStore
{
    AuthenticationSnapshot GetAuthenticationSnapshot();

    Task<CreatedApiToken> CreateTokenAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> RevokeTokenAsync(Guid id, CancellationToken cancellationToken = default);

    bool ValidateToken(string token);

    Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default);
}
