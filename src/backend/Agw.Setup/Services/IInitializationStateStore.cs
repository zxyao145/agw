using Agw.Setup.Contracts;

namespace Agw.Setup.Services;

public interface IInitializationStateStore
{
    InitializationSnapshot GetSnapshot();

    Task PersistAsync(SetupRequest request, CancellationToken cancellationToken = default);
}
