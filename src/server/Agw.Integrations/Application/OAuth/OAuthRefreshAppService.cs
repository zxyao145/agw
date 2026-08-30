using Agw.Integrations.Contracts.OAuth;

namespace Agw.Integrations.Application.OAuth;

public sealed class OAuthRefreshAppService
{
    private readonly OAuthAuthorizationAppService _authorizationService;

    public OAuthRefreshAppService(OAuthAuthorizationAppService authorizationService)
    {
        _authorizationService = authorizationService;
    }

    public Task<OAuthRefreshResponse> RefreshAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        return _authorizationService.RefreshAsync(connectionId, cancellationToken);
    }
}
