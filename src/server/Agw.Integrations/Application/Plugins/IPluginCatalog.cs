using Agw.Integrations.Domain.Plugins;

namespace Agw.Integrations.Application.Plugins;

public interface IPluginCatalog
{
    IReadOnlyList<PluginDefinition> List();

    PluginDefinition? Find(string pluginId);
}
