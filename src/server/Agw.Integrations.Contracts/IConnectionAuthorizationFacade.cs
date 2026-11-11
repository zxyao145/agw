namespace Agw.Integrations.Contracts;

public interface IConnectionAuthorizationFacade
{
    Task<string> GetAuthorizationHeaderAsync(Guid connectionId, CancellationToken cancellationToken);
}
