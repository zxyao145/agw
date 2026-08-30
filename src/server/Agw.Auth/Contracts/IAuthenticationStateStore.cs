namespace Agw.Auth.Contracts;

public interface IAuthenticationStateReader
{
    AuthenticationSnapshot GetAuthenticationSnapshot();
}

public interface IAuthenticationStateStore : IAuthenticationStateReader
{
    Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default);
}
