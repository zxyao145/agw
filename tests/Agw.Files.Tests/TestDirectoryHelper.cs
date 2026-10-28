namespace Agw.Files.Tests;

internal static class TestDirectoryHelper
{
    public static string Create(string category)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agw.slnx")))
        {
            directory = directory.Parent;
        }

        var repositoryRoot = directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Agw.slnx.");
        var path = Path.Combine(
            repositoryRoot,
            ".scratch",
            "Agw.Files.Tests",
            category,
            Guid.CreateVersion7().ToString("N")
        );
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            RecurseSubdirectories = true,
        };
        foreach (var filePath in Directory.EnumerateFiles(path, "*", enumerationOptions))
        {
            var attributes = File.GetAttributes(filePath);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(path, recursive: true);
    }
}
