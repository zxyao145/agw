namespace Agw.Files.Utils;

public static class PathUtil
{
    public static string ExpandTilde(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? "";

        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/") || path.StartsWith("~\\"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path.Substring(2));
        }

        return path;
    }
}
