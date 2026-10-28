using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Agw.Agents.Tests;

internal static class CodexAppServerFixture
{
    public static bool IsAvailable =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        && (RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64)
        && File.Exists(ExecutablePath());

    public static string ExecutablePath(string? overridePath = null, [CallerFilePath] string sourceFile = "")
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var platform =
            OperatingSystem.IsWindows() ? "win32"
            : OperatingSystem.IsMacOS() ? "darwin"
            : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"The Codex fixture does not support {RuntimeInformation.ProcessArchitecture}."
            ),
        };
        var targetTriple = (platform, architecture) switch
        {
            ("win32", "x64") => "x86_64-pc-windows-msvc",
            ("win32", "arm64") => "aarch64-pc-windows-msvc",
            ("darwin", "x64") => "x86_64-apple-darwin",
            ("darwin", "arm64") => "aarch64-apple-darwin",
            ("linux", "x64") => "x86_64-unknown-linux-musl",
            ("linux", "arm64") => "aarch64-unknown-linux-musl",
            _ => throw new PlatformNotSupportedException(
                $"The Codex fixture does not support {platform} {architecture}."
            ),
        };
        var executableName = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        return Path.GetFullPath(
            $"fixtures/codex-app-server/node_modules/@openai/codex-{platform}-{architecture}/vendor/{targetTriple}/bin/{executableName}",
            Path.GetDirectoryName(sourceFile)!
        );
    }
}
