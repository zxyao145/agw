using Agw.Shared.Runtime;

namespace Agw.Agents.ExternalAgents.Acp;

public sealed class AcpAgentOptions
{
    public const string SectionName = "AcpAgents";

    public Uri RegistryUrl { get; set; } = new("https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json");
    public TimeSpan RegistryRefreshInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan RegistryFetchTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public long RegistryMaxBytes { get; set; } = 8_388_608;
    public string? InstallRoot { get; set; }
    public bool AllowUnverifiedBinaries { get; set; }
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public long MaxDownloadBytes { get; set; } = 1_073_741_824;
    public TimeSpan InitializeTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan CancelGracePeriod { get; set; } = TimeSpan.FromSeconds(5);
    public int StderrTailLines { get; set; } = 20;

    public string ResolveInstallRoot(AgwDataPaths paths) =>
        string.IsNullOrEmpty(InstallRoot) ? Path.Combine(paths.Root, "acp-agents") : InstallRoot;
}
