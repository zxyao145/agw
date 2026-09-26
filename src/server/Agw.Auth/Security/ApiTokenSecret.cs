using System.Security.Cryptography;
using System.Text;

namespace Agw.Auth.Security;

/// <summary>
/// <para>API Token 密钥的生成与哈希：只保存哈希，查找时使用密钥开头的固定长度前缀。</para>
/// <para>Generates and hashes API token secrets: only the hash is stored, and lookups use a fixed-length leading prefix.</para>
/// </summary>
public static class ApiTokenSecret
{
    public const string SecretPrefix = "agw_";

    private const int LookupPrefixLength = 12;

    public static string Create()
    {
        var value = Convert
            .ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{SecretPrefix}{value}";
    }

    public static string GetLookupPrefix(string secret) => secret[..Math.Min(secret.Length, LookupPrefixLength)];

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
