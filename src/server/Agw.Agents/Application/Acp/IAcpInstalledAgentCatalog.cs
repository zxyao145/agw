using Agw.Agents.ExternalAgents.Acp;

namespace Agw.Agents.Application.Acp;

public interface IAcpInstalledAgentCatalog
{
    AcpInstalledAgent Find(string agentId, string? version);
    AcpInstalledAgent? FindVersion(string agentId, string version);
}

public sealed record AcpInstalledAgent(AcpInstallManifest Manifest, string Directory, string ExecutablePath);
