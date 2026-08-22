using Agw.Auth.Contracts;

namespace Agw.Auth.Application;

public interface IAuthenticationStateReader
{
    AuthenticationSnapshot GetAuthenticationSnapshot();
}

public interface IAuthenticationStateStore : IAuthenticationStateReader
{
    Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default);
}
