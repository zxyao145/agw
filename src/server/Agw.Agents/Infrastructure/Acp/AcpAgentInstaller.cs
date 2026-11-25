using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Agw.Agents.Application.Acp;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgentSdk.Internal;

namespace Agw.Agents.Infrastructure.Acp;

public sealed class AcpAgentInstaller : IAcpAgentInstaller
{
    private readonly AcpAgentOptions _options;
    private readonly string _root;
    private readonly AcpDownloadClient _downloads;
    private readonly IAcpInstalledAgentCatalog _catalog;
    private readonly TimeProvider _clock;
    private readonly ILogger<AcpAgentInstaller> _logger;

    public AcpAgentInstaller(
        IOptions<AcpAgentOptions> options,
        AgwDataPaths paths,
        AcpDownloadClient downloads,
        IAcpInstalledAgentCatalog catalog,
        TimeProvider clock,
        ILogger<AcpAgentInstaller> logger
    )
    {
        _options = options.Value;
        _root = _options.ResolveInstallRoot(paths);
        _downloads = downloads;
        _catalog = catalog;
        _clock = clock;
        _logger = logger;
    }

    public async Task<AcpInstallResult> InstallAsync(AcpRegistryAgent entry, CancellationToken ct)
    {
        if (
            !AcpAgentSettingsRules.IsRegistryAgentId(entry.Id)
            || !AcpAgentSettingsRules.IsRegistryVersion(entry.Version)
        )
            throw new AgwException(ErrorCodes.AcpInstallFailed, "Invalid registry agent ID or version.");
        var directory = Path.Combine(_root, entry.Id, entry.Version);
        if (_catalog.FindVersion(entry.Id, entry.Version) is { } installed)
            return Existing(installed);
        var stage = directory + ".partial-" + Guid.NewGuid().ToString("N");
        var start = _clock.GetTimestamp();
        try
        {
            Directory.CreateDirectory(stage);
            var manifest = await InstallDistributionAsync(stage, entry, ct);
            await WriteManifestAsync(stage, manifest, ct);
            ct.ThrowIfCancellationRequested();
            return Publish(stage, directory, manifest, start);
        }
        catch (Exception error)
            when (error
                    is IOException
                        or InvalidDataException
                        or ArgumentException
                        or JsonException
                        or UnauthorizedAccessException
                        or Win32Exception
                        or SharpCompress.Common.SharpCompressException
                || error is AgwException invalid && invalid.Code == ErrorCodes.InvalidParam.Code
            )
        {
            _logger.LogWarning(error, "Could not install ACP agent {AgentId} {Version}", entry.Id, entry.Version);
            throw new AgwException(
                ErrorCodes.AcpInstallFailed,
                $"Could not install {entry.Id} {entry.Version}: {error.Message}",
                error
            );
        }
        finally
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, true);
        }
    }

    private async Task<AcpInstallManifest> InstallDistributionAsync(
        string stage,
        AcpRegistryAgent entry,
        CancellationToken ct
    )
    {
        var npm = entry.Distribution.Npx is null ? null : await CommandUtil.FindAsync("npm", cancellationToken: ct);
        var uv = entry.Distribution.Uvx is null ? null : await CommandUtil.FindAsync("uv", cancellationToken: ct);
        var host = new AcpDistributionHost(
            AcpPlatform.Current,
            npm?.IsInstalled == true,
            uv?.IsInstalled == true,
            OperatingSystem.IsWindows()
        );
        var selection = AcpRegistryRules.SelectDistribution(entry, host, _options.AllowUnverifiedBinaries);
        if (selection.Kind is null)
            throw new AgwException(ErrorCodes.AcpInstallFailed, string.Join("; ", selection.Reasons));
        if (selection.Package is { } package && string.IsNullOrWhiteSpace(package.Package))
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Registry distribution '{selection.Kind}' is missing its package."
            );
        (string executable, string[] prefix) = selection switch
        {
            { Kind: AcpDistributionKinds.Binary, Binary: { } target } => (
                await InstallBinaryAsync(stage, target, ct),
                []
            ),
            { Kind: AcpDistributionKinds.Npx, Package: { } node } when npm?.Path is not null => (
                await AcpPackageInstaller.InstallNpmAsync(stage, npm, node.Package, ct),
                []
            ),
            { Kind: AcpDistributionKinds.Uvx, Package: { } python } when uv?.Path is not null =>
                await AcpPackageInstaller.InstallUvAsync(stage, uv, python.Package, ct),
            _ => throw new AgwException(ErrorCodes.AcpInstallFailed, "Unsupported ACP distribution."),
        };
        return CreateManifest(entry, selection, executable, prefix);
    }

    private static async Task WriteManifestAsync(string stage, AcpInstallManifest manifest, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(stage, manifest.Executable)))
            throw new AgwException(
                ErrorCodes.AcpInstallFailed,
                $"Installed executable '{manifest.Executable}' is missing."
            );
        await using var file = new FileStream(Path.Combine(stage, AcpInstallManifest.FileName), FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(file, manifest, JsonSerializerOptions.Web, ct);
    }

    private AcpInstallResult Publish(string stage, string directory, AcpInstallManifest manifest, long start)
    {
        try
        {
            Directory.Move(stage, directory);
        }
        catch (IOException error) when (Directory.Exists(directory))
        {
            _logger.LogWarning(error, "ACP installation directory {Directory} already exists", directory);
            if (_catalog.FindVersion(manifest.AgentId, manifest.Version) is { } winner)
                return Existing(winner);
            throw new AgwException(
                ErrorCodes.AcpInstallFailed,
                $"The existing installation at '{directory}' is incomplete or incompatible.",
                error
            );
        }
        _logger.LogInformation(
            "Installed ACP agent {AgentId} {Version} for {Platform} from {Distribution} at {Directory} in {ElapsedMs} ms",
            manifest.AgentId,
            manifest.Version,
            manifest.Platform,
            manifest.Distribution,
            directory,
            _clock.GetElapsedTime(start).TotalMilliseconds
        );
        return new(manifest.AgentId, manifest.Version, manifest.Distribution, directory, false);
    }

    private static AcpInstallResult Existing(AcpInstalledAgent agent) =>
        new(agent.Manifest.AgentId, agent.Manifest.Version, agent.Manifest.Distribution, agent.Directory, true);

    private AcpInstallManifest CreateManifest(
        AcpRegistryAgent entry,
        AcpDistributionSelection selection,
        string executable,
        IReadOnlyList<string> prefix
    )
    {
        var environment = selection.Binary?.Env ?? selection.Package?.Env;
        var env =
            environment
                ?.Where(pair => !AcpRegistryRules.IsReservedEnvironmentName(pair.Key))
                .ToDictionary(StringComparer.Ordinal)
            ?? new Dictionary<string, string>();
        return new(
            entry.Id,
            entry.Name,
            entry.Version,
            _options.RegistryUrl,
            selection.Kind!,
            AcpPlatform.InstallationPlatform,
            selection.Kind == AcpDistributionKinds.Npx ? "node" : "native",
            executable,
            [.. prefix, .. selection.Binary?.Args ?? selection.Package?.Args ?? []],
            env,
            _clock.GetUtcNow()
        );
    }

    private async Task<string> InstallBinaryAsync(string stage, AcpBinaryTarget target, CancellationToken ct)
    {
        var cmd = AcpRegistryRules.NormalizeCmd(target.Cmd);
        var download = Path.Combine(stage, Guid.NewGuid().ToString("N") + ".download");
        await DownloadReleaseAsync(download, target, ct);
        var path = target.Archive.AbsolutePath;
        var output = Path.Combine(stage, cmd);
        if (ArchiveExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            await AcpArchiveExtractor.ExtractAsync(download, path, stage, ct);
            File.Delete(download);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Move(download, output);
        }
        if (!File.Exists(output))
            throw new AgwException(ErrorCodes.AcpInstallFailed, $"Archive does not contain '{cmd}'.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                output,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead
                    | UnixFileMode.OtherExecute
            );
        return cmd;
    }

    private async Task DownloadReleaseAsync(string destination, AcpBinaryTarget target, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(_options.DownloadTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, target.Archive);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var file = new FileStream(destination, FileMode.CreateNew);
        try
        {
            using var response = await _downloads.SendAsync(request, linked.Token);
            response.EnsureSuccessStatusCode();
            var size = await AcpDownloadClient.CopyLimitedAsync(
                response.Content,
                file,
                _options.MaxDownloadBytes,
                hash,
                linked.Token
            );
            var verified = !string.IsNullOrWhiteSpace(target.Sha256);
            if (
                verified
                && !Convert
                    .ToHexString(hash.GetHashAndReset())
                    .Equals(target.Sha256, StringComparison.OrdinalIgnoreCase)
            )
                throw new AgwException(ErrorCodes.AcpDownloadFailed, $"SHA-256 mismatch for {target.Archive}.");
            _logger.LogInformation("Downloaded {Bytes} ACP binary bytes, SHA-256 verified: {Verified}", size, verified);
        }
        catch (Exception error)
            when (!ct.IsCancellationRequested
                && error is HttpRequestException or IOException or OperationCanceledException
            )
        {
            _logger.LogWarning(error, "Could not download ACP release {Archive}", target.Archive);
            throw new AgwException(
                ErrorCodes.AcpDownloadFailed,
                $"Could not download {target.Archive}: {error.Message}",
                error
            );
        }
    }

    private static readonly string[] ArchiveExtensions = [".zip", ".tgz", ".tar.gz", ".tbz2", ".tar.bz2"];
}
