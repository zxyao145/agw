namespace Agw.Agents.ExternalAgents.Acp;

public sealed record AcpInstallManifest(
    string AgentId,
    string Name,
    string Version,
    Uri RegistryUrl,
    string Distribution,
    string Platform,
    string Runtime,
    string Executable,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    DateTimeOffset InstalledAt
)
{
    public const string FileName = "agw-acp-agent.json";
}
