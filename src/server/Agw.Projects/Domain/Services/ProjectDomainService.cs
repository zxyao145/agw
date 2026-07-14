using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;

namespace Agw.Projects.Domain.Services;

public class ProjectDomainService
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
        "LPT9"
    ];

    private readonly TimeProvider _timeProvider;

    public ProjectDomainService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public bool TryPrepareForCreate(Project project, string user)
    {
        if (!TryFormatFolderName(project.Name, out var projectName))
        {
            return false;
        }

        project.Name = projectName;
        project.Workspace = string.IsNullOrWhiteSpace(project.Workspace)
            ? GetDefaultWorkspace(projectName)
            : project.Workspace.Trim();
        NormalizeEnvironmentVariables(project);
        project.Id = project.Id == Guid.Empty ? Guid.NewGuid() : project.Id;
        project.CreateBy = user;
        project.CreateTime = _timeProvider.GetUtcNow();
        return true;
    }

    public bool TryApplyUpdate(Project project, Action<Project> updateAction, string user)
    {
        updateAction(project);

        if (string.IsNullOrWhiteSpace(project.Name))
        {
            return false;
        }

        NormalizeEnvironmentVariables(project);
        project.UpdateBy = user;
        project.UpdateTime = _timeProvider.GetUtcNow();
        return true;
    }

    private static string GetDefaultWorkspace(string projectName) => $"~/.agw/{projectName}";

    private static void NormalizeEnvironmentVariables(Project project)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in project.EnvironmentVariables ?? [])
        {
            var normalizedName = name.Trim();
            if (string.IsNullOrEmpty(normalizedName)
                || normalizedName.Contains('=')
                || normalizedName.Contains('\0')
                || !normalized.TryAdd(normalizedName, value ?? string.Empty))
            {
                throw new AgwException(ErrorCodes.InvalidProjectEnvironmentVariableName);
            }
        }

        project.EnvironmentVariables = normalized;
    }

    private static bool TryFormatFolderName(string? value, out string folderName)
    {
        folderName = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var chars = value.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_')
            .ToArray();
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
