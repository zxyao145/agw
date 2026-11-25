using Agw.Agents.Application.Acp;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.ExternalAgents.Acp;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp;

internal sealed class AcpLaunchResolver
{
    private readonly IAcpInstalledAgentCatalog _catalog;

    public AcpLaunchResolver(IAcpInstalledAgentCatalog catalog)
    {
        _catalog = catalog;
    }

    public AcpLaunchSpec Resolve(AcpAgentSettings settings)
    {
        AcpAgentSettingsRules.Validate(settings);
        if (settings.Command is { } command)
            return new(command, settings.Args ?? [], new Dictionary<string, string>(), "command", null);

        var registry = settings.Registry!;
        var installed = _catalog.Find(registry.AgentId, registry.Version);
        var manifest = installed.Manifest;
        return manifest.Runtime == "node"
            ? new(
                "node",
                [installed.ExecutablePath, .. manifest.Args],
                manifest.Env,
                manifest.Distribution,
                manifest.Version
            )
            : new(installed.ExecutablePath, manifest.Args, manifest.Env, manifest.Distribution, manifest.Version);
    }
}
