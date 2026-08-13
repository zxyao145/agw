using Agw.Auth.Contracts;

namespace Agw.Auth.Application;

public interface IAuthenticationStateStore
{
    AuthenticationSnapshot GetAuthenticationSnapshot();

    Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default);
}
