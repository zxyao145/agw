using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;

namespace Agw.Agents.ExternalAgents.Pi;

internal sealed class PiRuntimePaths
{
    private PiRuntimePaths(string root, string configDirectory, string sessionDirectory)
    {
        Root = root;
        ConfigDirectory = configDirectory;
        SessionDirectory = sessionDirectory;
    }

    public string Root { get; }

    public string ConfigDirectory { get; }

    public string SessionDirectory { get; }

    public static PiRuntimePaths Create(AgwDataPaths dataPaths, string userId, string? userHome = null)
    {
        ArgumentNullException.ThrowIfNull(dataPaths);
        if (string.IsNullOrWhiteSpace(userId) || userId.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new AgwException(ErrorCodes.AuthenticationRequired, "A stable numeric user ID is required for Pi.");
        }

        userHome = string.IsNullOrWhiteSpace(userHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHome;
        var root = Path.GetFullPath(Path.Combine(dataPaths.Root, "external-agents", "pi", userId));
        var config = Path.GetFullPath(Path.Combine(userHome, ".pi", "agent"));
        var sessions = Path.GetFullPath(Path.Combine(root, "sessions"));
        EnsureChild(root, sessions);
        return new PiRuntimePaths(root, config, sessions);
    }

    public void EnsureCreated()
    {
        foreach (var directory in new[] { Root, SessionDirectory })
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }
        }
    }

    private static void EnsureChild(string root, string path)
    {
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(rootPrefix, comparison))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Pi runtime path escaped its user directory.");
        }
    }
}
