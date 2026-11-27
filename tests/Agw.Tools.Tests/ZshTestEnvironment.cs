namespace Agw.Tools.Tests;

internal static class ZshTestEnvironment
{
    public const string RequiresZsh = "此测试需要 /bin/zsh。This test requires /bin/zsh.";

    public static bool HasZsh => !OperatingSystem.IsWindows() && File.Exists("/bin/zsh");
}
