using Agw.Agents.ExternalAgents.Acp;

namespace Agw.Agents.Application.Acp;

public interface IAcpAgentInstaller
{
    Task<AcpInstallResult> InstallAsync(AcpRegistryAgent entry, CancellationToken ct);
}

public sealed record AcpInstallResult(
    string AgentId,
    string Version,
    string Distribution,
    string Directory,
    bool AlreadyInstalled
);
