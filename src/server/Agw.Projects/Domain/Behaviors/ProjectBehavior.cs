using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Domain.Rules;
using Agw.Shared.Exceptions;
using Agw.Shared.Tooling;
using Agw.Shared.Utils;

namespace Agw.Projects.Domain.Behaviors;

public sealed class ProjectBehavior
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

    private readonly Project _project;

    public ProjectBehavior(Project project)
    {
        _project = project;
    }

    public bool TryPrepareForCreate()
    {
        var project = _project;
        EnsureToolsAreValid(project);
        if (!TryFormatFolderName(project.Name, out var projectName))
        {
            return false;
        }

        project.Name = projectName;
        project.Id = project.Id == Guid.Empty ? Guid.CreateVersion7() : project.Id;
        project.Workspace = string.IsNullOrWhiteSpace(project.Workspace)
            ? ProjectDefaults.GetDefaultWorkspace(project.Id)
            : project.Workspace.Trim();
        NormalizeEnvironmentVariables(project);
        NormalizeAdditionalDirectories(project, []);
        return true;
    }

    public bool TryApplyUpdate(
        Action<Project> updateAction,
        IReadOnlyList<ProjectDirectory>? previousDirectories = null
    )
    {
        var project = _project;
        var originalType = project.Type;
        var originalName = project.Name;
        updateAction(project);
        EnsureToolsAreValid(project);

        if (string.IsNullOrWhiteSpace(project.Name))
        {
            project.Type = originalType;
            project.Name = originalName;
            return false;
        }

        if (
            originalType == ProjectType.DefaultBuiltIn
            && (project.Type != originalType || project.Name != originalName)
        )
        {
            project.Type = originalType;
            project.Name = originalName;
            return false;
        }

        if (string.IsNullOrWhiteSpace(project.Workspace))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Project primary directory is required.");
        }

        NormalizeEnvironmentVariables(project);
        NormalizeAdditionalDirectories(project, previousDirectories ?? []);
        return true;
    }

    /// <summary>
    /// <para>项目的 Tools 必须带有完整定义且名称不重复；Background Agents Tool Block 只能配置在 Agent 上。</para>
    /// <para>Project Tools need complete definitions with unique names; the Background Agents Tool Block belongs only to Agents.</para>
    /// </summary>
    private static void EnsureToolsAreValid(Project project)
    {
        var toolsError = ToolValueObjectValidation.GetError(project.Tools);
        if (toolsError != null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, toolsError);
        }

        if (
            project.Tools.Any(static value =>
                value is ToolBlockValue { Definition: BackgroundAgentsToolBlockDefinition }
            )
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool Block '{ToolBlockDefinitionNames.BackgroundAgents}' can only be configured on an Agent."
            );
        }
    }

    public bool TryDelete() => _project.Type != ProjectType.DefaultBuiltIn;

    /// <summary>
    /// <para>校验本次更新新增或改动的附加目录在外部文件系统中真实存在。</para>
    /// <para>Validates that additional directories added or changed by this update exist in the external file system.</para>
    /// </summary>
    /// <param name="existingDirectoryPaths">
    /// <para>由 Application 解析得到的真实存在的规范化目录路径集合。</para>
    /// <para>Normalized paths of directories that exist, resolved by Application code.</para>
    /// </param>
    /// <param name="previousDirectories">
    /// <para>更新前的附加目录集合，用于识别未改动因而无需校验的目录。</para>
    /// <para>Additional directories captured before the update, used to skip unchanged entries.</para>
    /// </param>
    public void EnsureAdditionalDirectoriesExist(
        IReadOnlySet<string> existingDirectoryPaths,
        IReadOnlyList<ProjectDirectory>? previousDirectories = null
    )
    {
        foreach (var directory in _project.AdditionalDirectories ?? [])
        {
            if (
                IsUnchangedDirectory(directory.Id, directory.Path, previousDirectories ?? [])
                || existingDirectoryPaths.Contains(directory.Path)
            )
            {
                continue;
            }

            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Additional directory does not exist: '{directory.Path}'."
            );
        }
    }

    public void RetainVisibleRelations(
        IReadOnlySet<Guid> visibleMcpToolServerIds,
        IReadOnlySet<Guid> visibleSkillIds,
        IReadOnlySet<Guid> visibleConnectionIds
    )
    {
        _project.ProjectMcpToolServers = _project
            .ProjectMcpToolServers.Where(relation => visibleMcpToolServerIds.Contains(relation.McpToolServerId))
            .ToList();
        _project.ProjectSkillRelations = _project
            .ProjectSkillRelations.Where(relation => visibleSkillIds.Contains(relation.SkillId))
            .ToList();
        _project.ProjectConnectionRelations = _project
            .ProjectConnectionRelations.Where(relation => visibleConnectionIds.Contains(relation.ConnectionId))
            .ToList();
    }

    private static void NormalizeAdditionalDirectories(Project project, IReadOnlyList<ProjectDirectory> previous)
    {
        var paths = new HashSet<string>(ProjectWorkspacePaths.Comparer)
        {
            ProjectWorkspacePaths.CreateSnapshot(project.Id, project.Workspace).Workspace,
        };
        var ids = new HashSet<Guid>();
        var normalized = new List<ProjectDirectory>();
        foreach (var directory in project.AdditionalDirectories ?? [])
        {
            if (directory == null)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "An additional directory must contain a path.");
            }

            var path = directory.Path?.Trim();
            if (
                string.IsNullOrWhiteSpace(path)
                || path.Length > 1000
                || path.Contains('\0')
                || !Path.IsPathFullyQualified(PathUtil.ExpandTilde(path))
            )
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "Additional directories require an absolute path or a ~/ path."
                );
            }

            var fullPath = ProjectWorkspacePaths.Normalize(path);
            if (!paths.Add(fullPath) || (directory.Id != Guid.Empty && !ids.Add(directory.Id)))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Project directories must be unique.");
            }

            var unchanged = IsUnchangedDirectory(directory.Id, fullPath, previous);
            normalized.Add(
                new ProjectDirectory { Id = unchanged ? directory.Id : Guid.CreateVersion7(), Path = fullPath }
            );
        }

        project.AdditionalDirectories = normalized;
    }

    private static bool IsUnchangedDirectory(
        Guid directoryId,
        string normalizedPath,
        IReadOnlyList<ProjectDirectory> previous
    )
    {
        var original = previous.FirstOrDefault(item => item.Id == directoryId);
        return original != null
            && ProjectWorkspacePaths.Comparer.Equals(ProjectWorkspacePaths.Normalize(original.Path), normalizedPath);
    }

    private static void NormalizeEnvironmentVariables(Project project)
    {
        project.EnvironmentVariables = EnvironmentVariableRules.Normalize(
            project.EnvironmentVariables,
            ErrorCodes.InvalidProjectEnvironmentVariableName
        );
    }

    private static bool TryFormatFolderName(string? value, out string folderName)
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
