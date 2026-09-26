namespace Agw.Integrations.Domain.Plugins;

/// <summary>
/// <para>一个连接所使用的插件、连接器与认证方案定义。</para>
/// <para>The plugin, connector and auth scheme definitions a connection uses.</para>
/// </summary>
public sealed class ResolvedIntegrationDefinition
{
    public ResolvedIntegrationDefinition(
        PluginDefinition plugin,
        ConnectorDefinition connector,
        AuthSchemeDefinition authScheme
    )
    {
        Plugin = plugin;
        Connector = connector;
        AuthScheme = authScheme;
    }

    public PluginDefinition Plugin { get; }
    public ConnectorDefinition Connector { get; }
    public AuthSchemeDefinition AuthScheme { get; }
}
