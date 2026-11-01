using Agw.Auth.Contracts;
using Agw.Files.Abstracts;
using Agw.Shared.Runtime;

namespace Agw.Projects.Application;

public sealed class ProjectFileSystemConfigurationProvider : IProjectFileSystemConfigurationProvider
{
    private readonly IProjectAppService _projectAppService;
    private readonly IUserInfoService _userInfoService;
    private readonly ProjectOwnershipCache _ownershipCache;

    public ProjectFileSystemConfigurationProvider(
        IProjectAppService projectAppService,
        IUserInfoService userInfoService,
        ProjectOwnershipCache ownershipCache
    )
    {
        _projectAppService = projectAppService;
        _userInfoService = userInfoService;
        _ownershipCache = ownershipCache;
    }

    public async Task<ProjectFileSystemConfiguration?> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = await _projectAppService.GetAsync(projectId);
        return project == null
            ? null
            : new ProjectFileSystemConfiguration(
                project.Name,
                project.Workspace,
                project.CreateBy,
                project
                    .AdditionalDirectories.Select(directory => new ProjectWorkspaceDirectory(
                        directory.Id,
                        directory.Path
                    ))
                    .ToArray()
            );
    }

    /// <summary>
    /// 所有权校验在目录列举、搜索等操作中按条目反复调用，因此命中短期缓存时不再查询数据库。
    /// Ownership is checked repeatedly per entry by listings and searches, so a short-lived cache avoids the query.
    /// </summary>
    public async Task<string?> GetOwnerUserIdAsync(Guid projectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = _userInfoService.RequiredUserId;
        if (await _ownershipCache.IsVerifiedAsync(userId, projectId, cancellationToken))
        {
            return userId;
        }

        var ownerUserId = await _projectAppService.GetOwnerUserIdAsync(projectId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(ownerUserId))
        {
            await _ownershipCache.MarkVerifiedAsync(userId, projectId, cancellationToken);
        }

        return ownerUserId;
    }
}
