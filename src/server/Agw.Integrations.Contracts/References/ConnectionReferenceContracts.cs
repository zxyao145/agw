namespace Agw.Integrations.Contracts.References;

public interface IConnectionReferenceFacade
{
    Task<IReadOnlySet<Guid>> FilterOwnedConnectionIdsAsync(
        IReadOnlyCollection<Guid> connectionIds,
        CancellationToken cancellationToken = default
    );
}
