using System.Runtime.InteropServices;

namespace Agw.Agents.ExternalAgents.Acp;

public static class AcpPlatform
{
    public static string? Current { get; } =
        Resolve(
            OperatingSystem.IsMacOS() ? OSPlatform.OSX
                : OperatingSystem.IsLinux() ? OSPlatform.Linux
                : OperatingSystem.IsWindows() ? OSPlatform.Windows
                : default,
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.RuntimeIdentifier
        );

    public static string InstallationPlatform => Current ?? RuntimeInformation.RuntimeIdentifier;

    public static string? Resolve(OSPlatform os, Architecture arch, string runtimeIdentifier)
    {
        var system =
            os == OSPlatform.OSX ? "darwin"
            : os == OSPlatform.Linux ? "linux"
            : os == OSPlatform.Windows ? "windows"
            : null;
        var cpu = arch switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            _ => null,
        };
        var suffix =
            os == OSPlatform.Linux && runtimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)
                ? "-musl"
                : "";
        return system is null || cpu is null ? null : $"{system}-{cpu}{suffix}";
    }
}
