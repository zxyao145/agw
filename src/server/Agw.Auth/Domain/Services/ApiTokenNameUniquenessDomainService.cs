using Agw.Auth.Domain.Repositories;
using Agw.Shared.Data.Entities.Auth;
using Agw.Shared.Exceptions;

namespace Agw.Auth.Domain.Services;

/// <summary>
/// <para>同一所有者的 API Token 名称按规范化形式唯一。</para>
/// <para>An owner's API token names are unique by their normalized form.</para>
/// </summary>
public sealed class ApiTokenNameUniquenessDomainService
{
    private readonly IApiTokenRepository _tokens;

    public ApiTokenNameUniquenessDomainService(IApiTokenRepository tokens)
    {
        _tokens = tokens;
    }

    public async Task EnsureNameAvailableAsync(ApiToken token, CancellationToken cancellationToken)
    {
        var exists = await _tokens
            .ExistsWithNormalizedNameAsync(token.CreateBy, token.NormalizedName, cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            throw new AgwException(ErrorCodes.ApiTokenNameAlreadyExists);
        }
    }
}
