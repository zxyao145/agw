using Agw.Auth.Contracts;
using Agw.Files.Abstracts;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public sealed class UserProjectInitializer : IUserProjectInitializer
{
    private readonly IProjectsDbContext _context;
    private readonly AgwDataPaths _dataPaths;
    private readonly IProjectWorkspaceAccess _workspaceAccess;

    public UserProjectInitializer(
        IProjectsDbContext context,
        AgwDataPaths dataPaths,
        IProjectWorkspaceAccess workspaceAccess
    )
    {
        _context = context;
        _dataPaths = dataPaths;
        _workspaceAccess = workspaceAccess;
    }

    public async Task EnsureDefaultsAsync(CancellationToken cancellationToken = default)
    {
        var owner = UserInfoUtil.RequiredUserId;
        foreach (var name in new[] { ProjectDefaults.DefaultBuiltInName, ProjectDefaults.A2AName })
        {
            if (
                await _context.Projects.AnyAsync(
                    project => project.CreateBy == owner && project.Name == name,
                    cancellationToken
                )
            )
                continue;
            var project = new Project
            {
                Id = Guid.CreateVersion7(),
                Name = name,
                Type = ProjectType.DefaultBuiltIn,
                CreateBy = owner,
            };
            project.Workspace = ProjectDefaults.GetDefaultWorkspace(
                project.Id,
                project.Name,
                _workspaceAccess.IsolationEnabled,
                _dataPaths.UserHomeDirectory
            );
            await EnsureDirectoriesCreatedAsync(project, cancellationToken).ConfigureAwait(false);
            _context.Projects.Add(project);
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// <para>保存前验证项目的主目录和附加目录，并创建缺失目录；默认主目录使用本实例用户目录下的绝对路径。</para>
    /// <para>Validates the primary and additional directories before creating missing directories; a default primary
    /// directory is stored as an absolute path under this instance's user home.</para>
    /// </summary>
    private async Task EnsureDirectoriesCreatedAsync(Project project, CancellationToken cancellationToken)
    {
        string[] paths =
        [
            PathUtil.ExpandTilde(project.Workspace!, _dataPaths.UserHomeDirectory),
            .. project.AdditionalDirectories.Select(directory =>
                PathUtil.ExpandTilde(directory.Path, _dataPaths.UserHomeDirectory)
            ),
        ];
        await _workspaceAccess.ValidateAsync(paths, project.Id, cancellationToken).ConfigureAwait(false);
        foreach (var path in paths)
        {
            ProjectWorkspacePaths.EnsureCreated(path);
        }
    }
}
