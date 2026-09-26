using Agw.Integrations.Domain.Plugins;
using Agw.Integrations.Domain.ValueObjects;

namespace Agw.Integrations.Domain.Repositories;

public interface IPluginInstallationRepository
{
    /// <summary>
    /// <para>返回当前所有者对该插件的安装，以及它在定义所用连接器与认证方案下的非密钥配置。</para>
    /// <para>Returns the current owner's installation of the plugin with its non-secret configuration for the definition's connector and auth scheme.</para>
    /// </summary>
    Task<PluginInstallationState?> FindAsync(
        ResolvedIntegrationDefinition definition,
        CancellationToken cancellationToken
    );
}
