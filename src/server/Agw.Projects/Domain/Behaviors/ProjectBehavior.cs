using Agw.Projects.Domain.Rules;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;

namespace Agw.Projects.Domain.Behaviors;

public sealed class ProjectBehavior
{
    private readonly Project _project;

    public ProjectBehavior(Project project)
    {
        _project = project;
    }

    public bool TryPrepareForCreate()
    {
        var project = _project;
        if (!ProjectRules.TryFormatFolderName(project.Name, out var projectName))
        {
            return false;
        }

        project.Name = projectName;
        project.Id = project.Id == Guid.Empty ? Guid.CreateVersion7() : project.Id;
        project.Workspace = string.IsNullOrWhiteSpace(project.Workspace)
            ? ProjectRules.GetDefaultWorkspace(project.Id)
            : project.Workspace.Trim();
        NormalizeEnvironmentVariables(project);
        return true;
    }

    public bool TryApplyUpdate(Action<Project> updateAction)
    {
        var project = _project;
        updateAction(project);

        if (string.IsNullOrWhiteSpace(project.Name))
        {
            return false;
        }

        NormalizeEnvironmentVariables(project);
        return true;
    }

    private static void NormalizeEnvironmentVariables(Project project)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in project.EnvironmentVariables ?? [])
        {
            var normalizedName = name.Trim();
            if (
                string.IsNullOrEmpty(normalizedName)
                || normalizedName.Contains('=')
                || normalizedName.Contains('\0')
                || !normalized.TryAdd(normalizedName, value ?? string.Empty)
            )
            {
                throw new AgwException(ErrorCodes.InvalidProjectEnvironmentVariableName);
            }
        }

        project.EnvironmentVariables = normalized;
    }
}
