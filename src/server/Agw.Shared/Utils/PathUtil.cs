namespace Agw.Shared.Utils;

public static class PathUtil
{
    public static string ExpandTilde(string? path)
    {
        return ExpandTilde(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public static string ExpandTilde(string? path, string userHome)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path ?? "";

        if (path == "~")
        {
            return userHome;
        }

        if (path.StartsWith("~/") || path.StartsWith("~\\"))
        {
            return Path.Combine(userHome, path[2..]);
        }

        return path;
    }
}
