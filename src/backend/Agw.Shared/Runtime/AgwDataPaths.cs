namespace Agw.Shared.Runtime;

public sealed class AgwDataPaths
{
    private AgwDataPaths(string root)
    {
        Root = root;
        StateFile = Path.Combine(root, "server-state.json");
        DatabaseFile = Path.Combine(root, "database", "agw.db");
        SkillsDirectory = Path.Combine(root, "skills");
        LogsDirectory = Path.Combine(root, "logs");
        TempDirectory = Path.Combine(root, "temp");
        KeysDirectory = Path.Combine(root, "keys");
    }

    public string Root { get; }

    public string StateFile { get; }

    public string DatabaseFile { get; }

    public string SkillsDirectory { get; }

    public string LogsDirectory { get; }

    public string TempDirectory { get; }

    public string KeysDirectory { get; }

    public static AgwDataPaths Resolve(string? configuredRoot, string userHome)
    {
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(userHome, "agw")
            : configuredRoot.Trim();

        return new AgwDataPaths(Path.GetFullPath(root));
    }

    public static AgwDataPaths ResolveFromEnvironment()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Resolve(Environment.GetEnvironmentVariable("AGW_DATA_DIR"), userHome);
    }

    public void EnsureCreated()
    {
        var directories = new[]
        {
            Root,
            Path.GetDirectoryName(DatabaseFile)!,
            SkillsDirectory,
            LogsDirectory,
            TempDirectory,
            KeysDirectory
        };
        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }
}
