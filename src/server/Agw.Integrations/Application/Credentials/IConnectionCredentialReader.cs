namespace Agw.Integrations.Application.Credentials;

/// <summary>
/// 按所有者和 Slot 读取、解密 Integration Credential。
/// </summary>
public interface IConnectionCredentialReader
{
    /// <summary>
    /// 读取指定 Connection 拥有的凭据。
    /// </summary>
    /// <param name="connectionId">Credential 所属的 Connection ID。</param>
    /// <param name="slot">用于标识凭据用途的 Slot。</param>
    /// <param name="cancellationToken">用于取消数据库读取操作的 Token。</param>
    /// <returns>解密后的凭据；指定 Slot 不存在时返回 <see langword="null"/>。</returns>
    Task<ResolvedCredential?> ReadConnectionAsync(Guid connectionId, string slot, CancellationToken cancellationToken);

    /// <summary>
    /// 读取指定 Plugin Installation 拥有的凭据。
    /// </summary>
    /// <param name="pluginInstallationId">Credential 所属的 Plugin Installation ID。</param>
    /// <param name="slot">用于标识 Connector、Auth Scheme 和字段的 Slot。</param>
    /// <param name="cancellationToken">用于取消数据库读取操作的 Token。</param>
    /// <returns>解密后的凭据；指定 Slot 不存在时返回 <see langword="null"/>。</returns>
    Task<ResolvedCredential?> ReadPluginInstallationAsync(
        Guid pluginInstallationId,
        string slot,
        CancellationToken cancellationToken
    );
}

public sealed class ResolvedCredential
{
    public string Value { get; init; } = string.Empty;
    public DateTimeOffset? ExpiresAtUtc { get; init; }
}
