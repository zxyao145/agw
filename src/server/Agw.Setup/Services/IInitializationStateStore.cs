using Agw.Setup.Contracts;

namespace Agw.Setup.Services;

public interface IInitializationStateStore
{
    bool IsInitialized { get; }

    Task PersistAsync(SetupRequest request, string passwordHash, CancellationToken cancellationToken = default);
}
