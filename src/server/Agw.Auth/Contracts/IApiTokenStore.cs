using System.ComponentModel;

namespace Agw.Auth.Contracts;

/// <summary>
/// <para>不可变的验证结果，HybridCache 的本地缓存直接复用同一实例。</para>
/// <para>An immutable validation result, so HybridCache's local cache reuses the same instance.</para>
/// </summary>
[ImmutableObject(true)]
public sealed record ApiTokenIdentity(
    string UserId,
    Guid? TokenId = null,
    string? DisplayName = null,
    string? LoginProvider = null
);

public interface IApiTokenStore
{
    Task<IReadOnlyList<ApiTokenSummary>> ListTokensAsync(CancellationToken cancellationToken = default);

    Task<CreatedApiToken> CreateTokenAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> RevokeTokenAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ApiTokenIdentity?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);
}
