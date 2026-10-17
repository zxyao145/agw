namespace Agw.Providers.Application.Persistence;

public interface IProviderModelDeletionCoordinator
{
    Task<bool> DeleteProviderAsync(
        Guid providerId,
        Func<IReadOnlyList<Guid>, CancellationToken, Task> ensureUnbindableAsync,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteModelAsync(
        Guid modelId,
        Func<IReadOnlyList<Guid>, CancellationToken, Task> ensureUnbindableAsync,
        CancellationToken cancellationToken = default
    );
}
