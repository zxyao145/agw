using System.Net;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Definitions.Domain.Rules;

public static class AcpRegistryRules
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH",
        "HOME",
        "USER",
        "LOGNAME",
        "SHELL",
        "TMPDIR",
        "TMP",
        "TEMP",
        "PATHEXT",
        "ComSpec",
        "SystemRoot",
    };
    private static readonly string[] ReservedPrefixes = ["LD_", "DYLD_", "NODE_", "NPM_", "UV_", "PYTHON"];

    public static bool IsReservedEnvironmentName(string name) =>
        ReservedNames.Contains(name)
        || ReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static string NormalizeCmd(string cmd)
    {
        var path = cmd?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':') || path.Contains('\0'))
            throw new AgwException(ErrorCodes.InvalidParam, "Expected a relative executable path.");
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();
        if (
            segments.Length == 0
            || segments.Contains("..")
            || segments.Any(segment => segment.EndsWith('.') || segment.EndsWith(' '))
        )
            throw new AgwException(ErrorCodes.InvalidParam, "Expected a path inside the installation directory.");
        return string.Join('/', segments);
    }

    public static AcpDistributionSelection SelectDistribution(
        AcpRegistryAgent entry,
        AcpDistributionHost host,
        bool allowUnverifiedBinaries
    )
    {
        var dist = entry.Distribution;
        var reasons = new List<string>();
        if (
            host.Platform is not null
            && dist.Binary?.TryGetValue(host.Platform, out var binary) == true
            && binary is not null
        )
        {
            var reason = BinaryRejection(binary, allowUnverifiedBinaries, host.IsWindows);
            if (reason is null)
                return new(AcpDistributionKinds.Binary, binary, null, reasons);
            reasons.Add("binary: " + reason);
        }
        else
            reasons.Add($"binary: no target for {host.Platform ?? "this platform"}");
        if (dist.Npx is not null && host.HasNpm)
            return new(AcpDistributionKinds.Npx, null, dist.Npx, reasons);
        reasons.Add(dist.Npx is null ? "npx: not provided" : "npx: npm was not found");
        if (dist.Uvx is not null && host.HasUv)
            return new(AcpDistributionKinds.Uvx, null, dist.Uvx, reasons);
        reasons.Add(dist.Uvx is null ? "uvx: not provided" : "uvx: uv was not found");
        return new(null, null, null, reasons);
    }

    private static string? BinaryRejection(AcpBinaryTarget target, bool allowUnverifiedBinaries, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(target.Sha256) && !allowUnverifiedBinaries)
            return "missing sha256";
        if (target.Archive is null || !IsAllowedDownloadUri(target.Archive))
            return "download URL is not allowed";
        try
        {
            var cmd = NormalizeCmd(target.Cmd);
            if (
                isWindows
                && (
                    cmd.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    || cmd.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                )
            )
                return "Windows command scripts are not supported";
            return null;
        }
        catch (AgwException error) when (error.Code == ErrorCodes.InvalidParam.Code)
        {
            return error.Message;
        }
    }

    public static bool IsAllowedDownloadUri(Uri uri) =>
        uri.IsAbsoluteUri
        && (
            uri.Scheme == Uri.UriSchemeHttps
            || uri.Scheme == Uri.UriSchemeHttp
                && (
                    uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || IPAddress.TryParse(uri.IdnHost, out var address) && IPAddress.IsLoopback(address)
                )
        );
}
