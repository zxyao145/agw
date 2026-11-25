using System.Text.Json;
using Agw.Agents.Application.Acp;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Semver;

namespace Agw.Agents.Infrastructure.Acp;

public sealed class AcpInstalledAgentCatalog : IAcpInstalledAgentCatalog
{
    private readonly string _root;
    private readonly ILogger<AcpInstalledAgentCatalog> _logger;

    public AcpInstalledAgentCatalog(
        IOptions<AcpAgentOptions> options,
        AgwDataPaths paths,
        ILogger<AcpInstalledAgentCatalog> logger
    )
    {
        _root = options.Value.ResolveInstallRoot(paths);
        _logger = logger;
    }

    public AcpInstalledAgent Find(string agentId, string? version)
    {
        var agentDirectory = Path.Combine(_root, agentId);
        if (AcpAgentSettingsRules.IsRegistryAgentId(agentId) && Directory.Exists(agentDirectory))
        {
            var installed = version is null ? FindLatest(agentDirectory, agentId) : FindVersion(agentId, version);
            if (installed is not null)
                return installed;
        }

        var host = Path.GetFileName(Environment.ProcessPath);
        throw new AgwException(
            ErrorCodes.AcpAgentNotInstalled,
            $"ACP agent '{agentId}' ({version ?? "any version"}) is not installed under '{_root}' for {AcpPlatform.InstallationPlatform}. Install it with '{host} acp install {agentId}'."
        );
    }

    private AcpInstalledAgent? FindLatest(string agentDirectory, string agentId) =>
        Directory
            .EnumerateDirectories(agentDirectory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(AcpAgentSettingsRules.IsRegistryVersion)
            .Select(candidate =>
                SemVersion.TryParse(candidate, SemVersionStyles.Strict, out var semver) ? semver : null
            )
            .OfType<SemVersion>()
            .OrderDescending(SemVersion.PrecedenceComparer)
            .Select(candidate => FindVersion(agentId, candidate.ToString()))
            .FirstOrDefault(candidate => candidate is not null);

    public AcpInstalledAgent? FindVersion(string agentId, string version)
    {
        if (!AcpAgentSettingsRules.IsRegistryAgentId(agentId) || !AcpAgentSettingsRules.IsRegistryVersion(version))
            return null;
        var directory = Path.Combine(_root, agentId, version);
        if (!Directory.Exists(directory))
            return null;
        try
        {
            using var file = File.OpenRead(Path.Combine(directory, AcpInstallManifest.FileName));
            var manifest = JsonSerializer.Deserialize<AcpInstallManifest>(file, JsonSerializerOptions.Web);
            if (
                manifest?.AgentId != agentId
                || manifest.Version != version
                || manifest.Platform != AcpPlatform.InstallationPlatform
                || manifest.Runtime is not ("native" or "node")
                || manifest.Env is null
                || manifest.Args is null
                || manifest.RegistryUrl is null
                || manifest.Distribution
                    is not (AcpDistributionKinds.Binary or AcpDistributionKinds.Npx or AcpDistributionKinds.Uvx)
                || string.IsNullOrEmpty(manifest.Name)
            )
            {
                _logger.LogWarning("Skipping incompatible ACP installation manifest in {Directory}", directory);
                return null;
            }
            var path = Path.Combine(directory, AcpRegistryRules.NormalizeCmd(manifest.Executable));
            if (!File.Exists(path))
            {
                _logger.LogWarning("Skipping ACP installation with missing executable {Executable}", path);
                return null;
            }
            return new(manifest, directory, path);
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or JsonException
                        or InvalidDataException
                        or AgwException
            )
        {
            _logger.LogWarning(error, "Skipping ACP installation in {Directory}", directory);
            return null;
        }
    }
}
