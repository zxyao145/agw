namespace Agw.Setup.Services;

public interface IInitializationStateStore
{
    bool IsInitialized { get; }

    Task PersistAsync(string passwordHash, CancellationToken cancellationToken = default);
}
