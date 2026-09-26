using System.Linq.Expressions;
using Agw.Auth.Contracts;
using Agw.Files.Abstracts;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Domain.Behaviors;
using Agw.Projects.Domain.Services;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Utils;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public class ProjectAppService : IProjectAppService
{
    private readonly IProjectsDbContext _dbContext;
    private readonly ProjectResourceBindingDomainService _resourceBindingDomainService;
    private readonly IProjectDeletionCoordinator _deletionCoordinator;
    private readonly ProjectResolver _projectResolver;
    private readonly IUserInfoService _userInfoService;
    private readonly IProjectFileSystemCacheInvalidator? _fileSystemCache;

    public ProjectAppService(
        IProjectsDbContext dbContext,
        ProjectResourceBindingDomainService resourceBindingDomainService,
        IProjectDeletionCoordinator deletionCoordinator,
        ProjectResolver projectResolver,
        IUserInfoService userInfoService,
        IProjectFileSystemCacheInvalidator? fileSystemCache = null
    )
    {
        _dbContext = dbContext;
        _resourceBindingDomainService = resourceBindingDomainService;
        _deletionCoordinator = deletionCoordinator;
        _projectResolver = projectResolver;
        _userInfoService = userInfoService;
        _fileSystemCache = fileSystemCache;
    }

    public async Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null)
    {
        var query = CreateProjectQuery(_userInfoService.RequiredUserId);
        if (predicate != null)
        {
            query = query.Where(predicate);
        }
        var projects = await query.ToListAsync();
        await _resourceBindingDomainService.RetainVisibleRelationsAsync(projects).ConfigureAwait(false);
        return projects.OrderByDescending(project => project.CreateTime).ThenBy(project => project.Name).ToList();
    }

    public async Task<IReadOnlyList<Project>> ListForCurrentUserAsync()
    {
        var projects = await CreateProjectQuery(_userInfoService.RequiredUserId).ToListAsync();
        await _resourceBindingDomainService.RetainVisibleRelationsAsync(projects).ConfigureAwait(false);
        return projects.OrderByDescending(project => project.CreateTime).ThenBy(project => project.Name).ToList();
    }

    public async Task<Project?> GetAsync(Guid id)
    {
        var project = await CreateProjectQuery(_userInfoService.RequiredUserId)
            .FirstOrDefaultAsync(project => project.Id == id);
        if (project != null)
        {
            await _resourceBindingDomainService.RetainVisibleRelationsAsync([project]).ConfigureAwait(false);
        }

        return project;
    }

    public async Task<Project?> GetForCurrentUserAsync(Guid id)
    {
        var project = await CreateProjectQuery(_userInfoService.RequiredUserId)
            .FirstOrDefaultAsync(project => project.Id == id);
        if (project != null)
        {
            await _resourceBindingDomainService.RetainVisibleRelationsAsync([project]).ConfigureAwait(false);
        }

        return project;
    }

    public Task<string?> GetOwnerUserIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = _userInfoService.RequiredUserId;
        return _dbContext
            .Projects.Where(project => project.Id == id && project.CreateBy == userId)
            .Select(project => project.CreateBy)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Project?> CreateAsync(
        Project project,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? connectionIds
    )
    {
        _ = _userInfoService.RequiredUserId;
        var behavior = new ProjectBehavior(project);
        if (!behavior.TryPrepareForCreate())
        {
            return null;
        }

        behavior.EnsureAdditionalDirectoriesExist(ResolveExistingAdditionalDirectoryPaths(project));
        EnsureWorkspaceDirectory(project.Workspace);
        await _dbContext.Projects.AddAsync(project);
        await SyncProjectMcpToolServerRelationsAsync(project.Id, mcpToolServerIds);
        await SyncProjectSkillRelationsAsync(project.Id, skillIds);
        await SyncProjectConnectionRelationsAsync(project.Id, connectionIds);
        await _dbContext.SaveChangesAsync();
        return await GetForCurrentUserAsync(project.Id);
    }

    public async Task<Project?> UpdateAsync(
        Guid id,
        Action<Project> updateAction,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? connectionIds
    )
    {
        var user = _userInfoService.RequiredUserId;
        var existing = await _dbContext.Projects.FirstOrDefaultAsync(
            project => project.Id == id && project.CreateBy == user,
            CancellationToken.None
        );
        if (existing == null)
        {
            return null;
        }

        var originalDirectories = existing.AdditionalDirectories.ToArray();
        var behavior = new ProjectBehavior(existing);
        if (!behavior.TryApplyUpdate(updateAction, originalDirectories))
        {
            return null;
        }

        behavior.EnsureAdditionalDirectoriesExist(
            ResolveExistingAdditionalDirectoryPaths(existing),
            originalDirectories
        );
        EnsureWorkspaceDirectory(existing.Workspace);
        // Preserve audit stamping even when only bindings change or the update is a no-op.
        _dbContext.Projects.Entry(existing).Property(project => project.Name).IsModified = true;
        if (mcpToolServerIds != null)
        {
            await SyncProjectMcpToolServerRelationsAsync(existing.Id, mcpToolServerIds);
        }
        if (skillIds != null)
        {
            await SyncProjectSkillRelationsAsync(existing.Id, skillIds);
        }
        if (connectionIds != null)
        {
            await SyncProjectConnectionRelationsAsync(existing.Id, connectionIds);
        }
        await _dbContext.SaveChangesAsync();
        _fileSystemCache?.Invalidate(existing.Id);
        return await GetForCurrentUserAsync(existing.Id);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await _dbContext.Projects.FirstOrDefaultAsync(
            project => project.Id == id && project.CreateBy == _userInfoService.RequiredUserId,
            CancellationToken.None
        );
        if (existing == null || !new ProjectBehavior(existing).TryDelete())
        {
            return false;
        }

        var deleted = await _deletionCoordinator.DeleteProjectAsync(
            new ProjectDeletionTarget(existing.Id, existing.CreateBy!)
        );
        if (!deleted)
        {
            return false;
        }

        _fileSystemCache?.Invalidate(id);
        return true;
    }

    public async Task<string?> GetProjectExtraSettingAsync(Guid? projectId)
    {
        var project = await _projectResolver.ResolveAsync(projectId);
        return project?.ExtraSetting;
    }

    public async Task<Guid?> ResolveProjectIdAsync(Guid? projectId)
    {
        var project = await _projectResolver.ResolveAsync(projectId);
        return project?.Id;
    }

    /// <summary>
    /// <para>返回项目附加目录中在本机文件系统上真实存在的规范化路径。</para>
    /// <para>Returns the normalized additional directory paths that exist on the local file system.</para>
    /// </summary>
    private static IReadOnlySet<string> ResolveExistingAdditionalDirectoryPaths(Project project) =>
        project
            .AdditionalDirectories.Select(directory => directory.Path)
            .Where(Directory.Exists)
            .ToHashSet(ProjectWorkspacePaths.Comparer);

    private static void EnsureWorkspaceDirectory(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return;
        }

        Directory.CreateDirectory(PathUtil.ExpandTilde(workspace.Trim()));
    }

    private async Task SyncProjectMcpToolServerRelationsAsync(Guid projectId, IEnumerable<Guid>? mcpToolServerIds)
    {
        var currentIds = await _dbContext
            .ProjectMcpToolServers.Where(relation => relation.ProjectId == projectId)
            .Select(relation => relation.McpToolServerId)
            .ToListAsync();
        var (addedIds, removedIds) = await _resourceBindingDomainService
            .PlanMcpToolServerBindingsAsync(currentIds, mcpToolServerIds)
            .ConfigureAwait(false);
        if (removedIds.Count > 0)
        {
            var removedRelations = await _dbContext
                .ProjectMcpToolServers.Where(relation =>
                    relation.ProjectId == projectId && removedIds.Contains(relation.McpToolServerId)
                )
                .ToListAsync();
            foreach (var relation in removedRelations)
            {
                _dbContext.ProjectMcpToolServers.Remove(relation);
            }
        }

        foreach (var resourceId in addedIds)
        {
            await _dbContext.ProjectMcpToolServers.AddAsync(
                new ProjectMcpServerRelation { ProjectId = projectId, McpToolServerId = resourceId }
            );
        }
    }

    private async Task SyncProjectSkillRelationsAsync(Guid projectId, IEnumerable<Guid>? skillIds)
    {
        var currentIds = await _dbContext
            .ProjectSkillRelations.Where(relation => relation.ProjectId == projectId)
            .Select(relation => relation.SkillId)
            .ToListAsync();
        var (addedIds, removedIds) = await _resourceBindingDomainService
            .PlanSkillBindingsAsync(currentIds, skillIds)
            .ConfigureAwait(false);
        if (removedIds.Count > 0)
        {
            var removedRelations = await _dbContext
                .ProjectSkillRelations.Where(relation =>
                    relation.ProjectId == projectId && removedIds.Contains(relation.SkillId)
                )
                .ToListAsync();
            foreach (var relation in removedRelations)
            {
                _dbContext.ProjectSkillRelations.Remove(relation);
            }
        }

        foreach (var resourceId in addedIds)
        {
            await _dbContext.ProjectSkillRelations.AddAsync(
                new ProjectSkillRelation { ProjectId = projectId, SkillId = resourceId }
            );
        }
    }

    private IQueryable<Project> CreateProjectQuery(string ownerUserId)
    {
        IQueryable<Project> query = _dbContext
            .Projects.Include(project => project.ProjectMcpToolServers)
            .Include(project => project.ProjectSkillRelations)
            .Include(project => project.ProjectConnectionRelations)
            .Where(project => project.CreateBy == ownerUserId);
        return query.AsNoTracking().AsSplitQuery();
    }

    private async Task SyncProjectConnectionRelationsAsync(Guid projectId, IEnumerable<Guid>? connectionIds)
    {
        var currentIds = await _dbContext
            .ProjectConnectionRelations.Where(relation => relation.ProjectId == projectId)
            .Select(relation => relation.ConnectionId)
            .ToListAsync();
        var (addedIds, removedIds) = await _resourceBindingDomainService
            .PlanConnectionBindingsAsync(currentIds, connectionIds)
            .ConfigureAwait(false);
        if (removedIds.Count > 0)
        {
            var removedRelations = await _dbContext
                .ProjectConnectionRelations.Where(relation =>
                    relation.ProjectId == projectId && removedIds.Contains(relation.ConnectionId)
                )
                .ToListAsync();
            foreach (var relation in removedRelations)
            {
                _dbContext.ProjectConnectionRelations.Remove(relation);
            }
        }

        foreach (var resourceId in addedIds)
        {
            await _dbContext.ProjectConnectionRelations.AddAsync(
                new ProjectConnectionRelation { ProjectId = projectId, ConnectionId = resourceId }
            );
        }
    }
}
