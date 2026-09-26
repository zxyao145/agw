using Agw.Shared.Data.Entities.Integrations;

namespace Agw.Integrations.Domain.ValueObjects;

/// <summary>
/// <para>插件安装与它在某个连接器、认证方案下已解码的非密钥配置。</para>
/// <para>A plugin installation together with its decoded non-secret configuration for one connector and auth scheme.</para>
/// </summary>
public sealed record PluginInstallationState
{
    public required PluginInstallation Installation { get; init; }

    public required IReadOnlyDictionary<string, string?> ScopedConfiguration { get; init; }
}
