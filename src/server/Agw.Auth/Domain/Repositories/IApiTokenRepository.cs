namespace Agw.Auth.Domain.Repositories;

public interface IApiTokenRepository
{
    Task<bool> ExistsWithNormalizedNameAsync(
        string? ownerUserId,
        string normalizedName,
        CancellationToken cancellationToken
    );
}
