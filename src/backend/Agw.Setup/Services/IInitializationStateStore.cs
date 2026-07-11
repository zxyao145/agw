using Agw.Setup.Contracts;

namespace Agw.Setup.Services;

public interface IInitializationStateStore
{
    InitializationSnapshot GetSnapshot();

    Task PersistAsync(SetupRequest request, string passwordHash, CancellationToken cancellationToken = default);

    Task<CreatedApiToken> CreateTokenAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> RevokeTokenAsync(Guid id, CancellationToken cancellationToken = default);

    bool ValidateToken(string token);

    Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default);
}
