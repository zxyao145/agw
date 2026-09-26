using Agw.Integrations.Domain.Behaviors;
using Agw.Integrations.Domain.Plugins;
using Agw.Integrations.Domain.Repositories;
using Agw.Shared.Data.Entities.Integrations;

namespace Agw.Integrations.Domain.Services;

/// <summary>
/// <para>连接依赖所有者对同一插件的安装：安装是否启用并配置完成，决定连接能否就绪；安装变化时，受影响的连接重新设置状态。</para>
/// <para>Connections depend on the owner's installation of their plugin: whether it is enabled and configured decides readiness, and a change to it resets the affected connections.</para>
/// </summary>
public sealed class PluginInstallationReadinessDomainService
{
    private readonly IPluginInstallationRepository _installations;
    private readonly IConnectionRepository _connections;

    public PluginInstallationReadinessDomainService(
        IPluginInstallationRepository installations,
        IConnectionRepository connections
    )
    {
        _installations = installations;
        _connections = connections;
    }

    /// <summary>
    /// <para>判断连接所需的插件安装是否已启用并配置完成；认证方案不要求安装字段时视为已配置。同时返回找到的安装，供调用方检查其凭据。</para>
    /// <para>Checks whether the plugin installation a connection needs is enabled and configured, treating schemes without setup fields as configured, and returns the installation found so the caller can check its credentials.</para>
    /// </summary>
    public async Task<(bool Configured, PluginInstallation? Installation)> ResolveInstallationAsync(
        ResolvedIntegrationDefinition definition,
        CancellationToken cancellationToken
    )
    {
        if (definition.AuthScheme.InstallationFields.Count == 0)
        {
            return (true, null);
        }

        var state = await _installations.FindAsync(definition, cancellationToken).ConfigureAwait(false);
        if (state == null || !state.Installation.Enabled)
        {
            return (false, state?.Installation);
        }

        var configured = new PluginInstallationBehavior(state.Installation).IsConfiguredFor(
            definition,
            state.ScopedConfiguration
        );
        return (configured, state.Installation);
    }

    /// <summary>
    /// <para>安装停用时影响所有者使用该插件的全部连接，启用时只影响使用同一连接器与认证方案的连接；返回受影响的连接。</para>
    /// <para>A disabled installation affects every connection of the owner using the plugin, while an enabled one affects only those using the same connector and auth scheme; returns the affected connections.</para>
    /// </summary>
    public async Task<IReadOnlyList<Connection>> ResetAffectedConnectionsAsync(
        PluginInstallation installation,
        ResolvedIntegrationDefinition definition,
        IReadOnlyDictionary<string, string?> scopedConfiguration,
        CancellationToken cancellationToken
    )
    {
        var connections = await _connections
            .ListTrackedForPluginAsync(installation.PluginId, cancellationToken)
            .ConfigureAwait(false);
        var affected = installation.Enabled
            ? connections
                .Where(connection =>
                    connection.ConnectorId == definition.Connector.Id
                    && connection.AuthSchemeId == definition.AuthScheme.Id
                )
                .ToList()
            : connections.ToList();
        var installationConfigured =
            installation.Enabled
            && new PluginInstallationBehavior(installation).IsConfiguredFor(definition, scopedConfiguration);
        foreach (var connection in affected)
        {
            new ConnectionBehavior(connection).ResetAfterInstallationChange(
                installationConfigured,
                definition.AuthScheme.Type
            );
        }

        return affected;
    }
}
