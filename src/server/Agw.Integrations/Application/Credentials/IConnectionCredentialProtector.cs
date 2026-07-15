namespace Agw.Integrations.Application.Credentials;

/// <summary>
/// 对 Integration Credential 的明文值进行加密保护和解密还原。
/// </summary>
public interface IConnectionCredentialProtector
{
    /// <summary>
    /// 将凭据明文转换为可持久化的受保护值。
    /// </summary>
    /// <param name="plaintext">需要保护的凭据明文。</param>
    /// <returns>可写入 Credential ProtectedValue 字段的受保护值。</returns>
    string Protect(string plaintext);

    /// <summary>
    /// 将持久化的受保护值还原为凭据明文。
    /// </summary>
    /// <param name="protectedValue">从 Credential ProtectedValue 字段读取的受保护值。</param>
    /// <returns>解密后的凭据明文。</returns>
    string Unprotect(string protectedValue);
}
