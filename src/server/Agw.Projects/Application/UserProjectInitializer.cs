using Agw.Auth.Contracts;
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

    public UserProjectInitializer(IProjectsDbContext context, AgwDataPaths dataPaths)
    {
        _context = context;
        _dataPaths = dataPaths;
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
            EnsureDirectoriesCreated(project);
            _context.Projects.Add(project);
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// <para>保存前创建项目的有效主目录和配置中的附加目录；<c>Workspace</c> 为空时按项目 ID 使用默认路径，<c>~</c> 用本实例的用户目录展开。</para>
    /// <para>Creates the Project's effective primary directory and configured additional directories before saving; an
    /// empty <c>Workspace</c> uses the default path for the Project ID, and <c>~</c> expands with this instance's user
    /// home.</para>
    /// </summary>
    private void EnsureDirectoriesCreated(Project project)
    {
        var workspace = string.IsNullOrWhiteSpace(project.Workspace)
            ? ProjectDefaults.GetDefaultWorkspace(project.Id)
            : project.Workspace;
        ProjectWorkspacePaths.EnsureCreated(PathUtil.ExpandTilde(workspace, _dataPaths.UserHomeDirectory));
        foreach (var directory in project.AdditionalDirectories)
        {
            ProjectWorkspacePaths.EnsureCreated(PathUtil.ExpandTilde(directory.Path, _dataPaths.UserHomeDirectory));
        }
    }
}
