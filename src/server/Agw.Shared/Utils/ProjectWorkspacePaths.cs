using System.Text.Json;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;

namespace Agw.Shared.Utils;

public static class ProjectWorkspacePaths
{
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(PathUtil.ExpandTilde(path.Trim())));

    public static ProjectWorkspaceSnapshot CreateSnapshot(
        Guid projectId,
        string? workspace,
        IEnumerable<ProjectWorkspaceDirectory>? additionalDirectories = null
    )
    {
        var primary = Normalize(string.IsNullOrWhiteSpace(workspace) ? $"~/.agw/projects/{projectId:N}" : workspace);
        var directories = (additionalDirectories ?? [])
            .Select(directory => directory with { Path = Normalize(directory.Path) })
            .ToArray();
        var fingerprintData = JsonSerializer.Serialize(
            new
            {
                Workspace = OperatingSystem.IsWindows() ? primary.ToUpperInvariant() : primary,
                Directories = directories
                    .OrderBy(directory => directory.Id)
                    .Select(directory => new
                    {
                        directory.Id,
                        Path = OperatingSystem.IsWindows() ? directory.Path.ToUpperInvariant() : directory.Path,
                    }),
            }
        );
        return new ProjectWorkspaceSnapshot(
            primary,
            Array.AsReadOnly(directories),
            Sha256Util.HashUtf8Hex(fingerprintData)
        );
    }

    public static void EnsureAvailable(Guid projectId, ProjectWorkspaceSnapshot snapshot)
    {
        // The legacy default root is created on demand; explicit additional roots are never created.
        if (Comparer.Equals(snapshot.Workspace, Normalize($"~/.agw/projects/{projectId:N}")))
        {
            Directory.CreateDirectory(snapshot.Workspace);
        }
        foreach (
            var path in snapshot.AdditionalDirectories.Select(directory => directory.Path).Prepend(snapshot.Workspace)
        )
        {
            if (!Directory.Exists(path))
            {
                throw new AgwException(ErrorCodes.ResourceNotFound, $"Project directory is unavailable: '{path}'.");
            }
        }
    }
}
