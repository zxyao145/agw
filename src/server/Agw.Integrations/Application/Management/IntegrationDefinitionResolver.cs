using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Exceptions;

namespace Agw.Integrations.Application.Management;

internal static class IntegrationDefinitionResolver
{
    public static ResolvedIntegrationDefinition Resolve(
        IPluginCatalog catalog,
        string pluginId,
        string connectorId,
        string authSchemeId)
    {
        if (TryResolve(catalog, pluginId, connectorId, authSchemeId, out var resolved))
        {
            return resolved!;
        }

        throw new AgwException(ErrorCodes.IntegrationDefinitionNotFound);
    }

    public static bool TryResolve(
        IPluginCatalog catalog,
        string pluginId,
        string connectorId,
        string authSchemeId,
        out ResolvedIntegrationDefinition? resolved)
    {
        var normalizedPluginId = NormalizeId(pluginId);
        var normalizedConnectorId = NormalizeId(connectorId);
        var normalizedAuthSchemeId = NormalizeId(authSchemeId);
        var plugin = catalog.Find(normalizedPluginId);
        var connector = plugin?.Connectors.FirstOrDefault(item =>
            string.Equals(item.Id, normalizedConnectorId, StringComparison.OrdinalIgnoreCase));
        var authScheme = connector?.AuthSchemes.FirstOrDefault(item =>
            string.Equals(item.Id, normalizedAuthSchemeId, StringComparison.OrdinalIgnoreCase));
        if (plugin == null || connector == null || authScheme == null)
        {
            resolved = null;
            return false;
        }

        resolved = new ResolvedIntegrationDefinition(plugin, connector, authScheme);
        return true;
    }

    public static string NormalizeId(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}

internal sealed class ResolvedIntegrationDefinition
{
    public ResolvedIntegrationDefinition(
        PluginDefinition plugin,
        ConnectorDefinition connector,
        AuthSchemeDefinition authScheme)
    {
        Plugin = plugin;
        Connector = connector;
        AuthScheme = authScheme;
    }

    public PluginDefinition Plugin { get; }
    public ConnectorDefinition Connector { get; }
    public AuthSchemeDefinition AuthScheme { get; }
}
