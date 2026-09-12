namespace Agw.Auth.Contracts;

public interface IServerAuthStatePersistence
{
    Task<AuthenticationSnapshot?> ReadAsync(CancellationToken cancellationToken = default);
    Task InitializeAsync(string passwordHash, CancellationToken cancellationToken = default);
    Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default);
}
