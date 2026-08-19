using Agw.Setup.Contracts;

namespace Agw.Setup.Services;

public interface IInitializationStateStore
{
    bool IsInitialized { get; }

    Task PersistAsync(
        SetupConfiguration configuration,
        string passwordHash,
        CancellationToken cancellationToken = default
    );
}
