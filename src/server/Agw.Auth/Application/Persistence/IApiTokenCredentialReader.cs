using Agw.Auth.Contracts;

namespace Agw.Auth.Application.Persistence;

/// <summary>
/// <para>在认证边界按密钥解析 API Token 的所有者：查找跨越所有用户，只有密钥哈希匹配时才返回身份。</para>
/// <para>Resolves an API token's owner from its secret at the authentication boundary: the lookup spans all users and returns an identity only when the secret hash matches.</para>
/// </summary>
public interface IApiTokenCredentialReader
{
    Task<ApiTokenIdentity?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);
}
