using Agw.Shared.Data.Entities.Integrations;

namespace Agw.Integrations.Domain.Repositories;

/// <summary>
/// <para>读取当前所有者的连接。</para>
/// <para>Reads the current owner's connections.</para>
/// </summary>
public interface IConnectionRepository
{
    Task<bool> AliasExistsAsync(string alias, CancellationToken cancellationToken);

    /// <summary>
    /// <para>返回使用某个插件的连接，并跟踪它们及其凭据的变化。</para>
    /// <para>Returns the connections that use a plugin, tracking them and their credentials for changes.</para>
    /// </summary>
    Task<IReadOnlyList<Connection>> ListTrackedForPluginAsync(string pluginId, CancellationToken cancellationToken);
}
