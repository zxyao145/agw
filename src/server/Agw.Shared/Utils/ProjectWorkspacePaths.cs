using System.Text.Json;
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

    /// <summary>
    /// <para>规范化目录路径并创建缺失的目录及其上级目录；已有目录及其内容保持不变，创建失败时异常直接抛出。</para>
    /// <para>Normalizes the directory path and creates the directory and its missing parents; an existing directory and
    /// its contents stay unchanged, and a creation failure propagates.</para>
    /// </summary>
    public static string EnsureCreated(string directoryPath)
    {
        var path = Normalize(directoryPath);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// <para>为一次执行准备快照中的主目录和每个附加目录。</para>
    /// <para>Prepares the primary directory and every additional directory of the snapshot for one execution.</para>
    /// </summary>
    public static void EnsureAvailable(ProjectWorkspaceSnapshot snapshot)
    {
        EnsureCreated(snapshot.Workspace);
        foreach (var directory in snapshot.AdditionalDirectories)
        {
            EnsureCreated(directory.Path);
        }
    }
}
