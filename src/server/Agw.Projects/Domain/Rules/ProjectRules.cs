namespace Agw.Projects.Domain.Rules;

public static class ProjectRules
{
    private static readonly char[] TrimmedFolderCharacters = ['_', '.', ' '];
    private static readonly string[] ReservedWindowsFolderNames =
    [
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
    ];

    public static string GetDefaultWorkspace(Guid projectId) => $"~/.agw/projects/{projectId:N}";

    public static bool TryFormatFolderName(string? value, out string folderName)
    {
        folderName = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var chars = value.Trim().Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray();
        folderName = new string(chars);

        while (folderName.Contains("__", StringComparison.Ordinal))
        {
            folderName = folderName.Replace("__", "_", StringComparison.Ordinal);
        }

        folderName = folderName.Trim(TrimmedFolderCharacters);
        if (string.IsNullOrWhiteSpace(folderName) || folderName is "." or ".." || folderName.Length > 255)
        {
            return false;
        }

        var baseName = folderName.Split('.')[0];
        return !ReservedWindowsFolderNames.Contains(baseName, StringComparer.OrdinalIgnoreCase);
    }
}
