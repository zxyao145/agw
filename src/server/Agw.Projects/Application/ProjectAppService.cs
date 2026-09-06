using System.Linq.Expressions;
using Agw.Agents.Contracts.Catalog;
using Agw.Auth.Contracts;
using Agw.Files.Abstracts;
using Agw.Files.Utils;
using Agw.Integrations.Contracts.References;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Domain.Behaviors;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Skills.Contracts.References;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public class ProjectAppService : IProjectAppService
{
    private readonly IProjectsDbContext _dbContext;
    private readonly IAgentCatalogFacade _agentCatalog;
    private readonly ISkillReferenceFacade _skillReferences;
    private readonly IConnectionReferenceFacade _connectionReferences;
    private readonly IProjectDeletionCoordinator _deletionCoordinator;
    private readonly ProjectResolver _projectResolver;
    private readonly IUserInfoService _userInfoService;
    private readonly IProjectFileSystemCacheInvalidator? _fileSystemCache;

    public ProjectAppService(
        IProjectsDbContext dbContext,
        IAgentCatalogFacade agentCatalog,
        ISkillReferenceFacade skillReferences,
        IConnectionReferenceFacade connectionReferences,
        IProjectDeletionCoordinator deletionCoordinator,
        ProjectResolver projectResolver,
        IUserInfoService userInfoService,
        IProjectFileSystemCacheInvalidator? fileSystemCache = null
    )
    {
        _dbContext = dbContext;
        _agentCatalog = agentCatalog;
        _skillReferences = skillReferences;
        _connectionReferences = connectionReferences;
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
        await FilterVisibleReferenceRelationsAsync(projects).ConfigureAwait(false);
        return projects.OrderByDescending(project => project.CreateTime).ThenBy(project => project.Name).ToList();
    }

    public async Task<IReadOnlyList<Project>> ListForCurrentUserAsync()
    {
        var projects = await CreateProjectQuery(_userInfoService.RequiredUserId).ToListAsync();
        await FilterVisibleReferenceRelationsAsync(projects).ConfigureAwait(false);
        return projects.OrderByDescending(project => project.CreateTime).ThenBy(project => project.Name).ToList();
    }

    public async Task<Project?> GetAsync(Guid id)
    {
        var project = await CreateProjectQuery(_userInfoService.RequiredUserId)
            .FirstOrDefaultAsync(project => project.Id == id);
        if (project != null)
        {
            await FilterVisibleReferenceRelationsAsync([project]).ConfigureAwait(false);
        }

        return project;
    }

    public async Task<Project?> GetForCurrentUserAsync(Guid id)
    {
        var project = await CreateProjectQuery(_userInfoService.RequiredUserId)
            .FirstOrDefaultAsync(project => project.Id == id);
        if (project != null)
        {
            await FilterVisibleReferenceRelationsAsync([project]).ConfigureAwait(false);
        }

        return project;
    }

    public Task<Project?> CreateAsync(Project project) => CreateAsync(project, null, null, null);

    public async Task<Project?> CreateAsync(
        Project project,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? connectionIds
    )
    {
        _ = _userInfoService.RequiredUserId;
        if (!new ProjectBehavior(project).TryPrepareForCreate())
        {
            return null;
        }

        EnsureWorkspaceDirectory(project.Workspace);
        await _dbContext.Projects.AddAsync(project);
        await SyncProjectMcpToolServerRelationsAsync(project.Id, mcpToolServerIds);
        await SyncProjectSkillRelationsAsync(project.Id, skillIds);
        await SyncProjectConnectionRelationsAsync(project.Id, connectionIds);
        await _dbContext.SaveChangesAsync();
        return await GetForCurrentUserAsync(project.Id);
    }

    public Task<Project?> UpdateAsync(Guid id, Action<Project> updateAction) =>
        UpdateAsync(id, updateAction, null, null, null);

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

        var originalType = existing.Type;
        var originalName = existing.Name;
        if (!new ProjectBehavior(existing).TryApplyUpdate(updateAction))
        {
            return null;
        }

        if (
            originalType == ProjectType.DefaultBuiltIn
            && (existing.Type != originalType || existing.Name != originalName)
        )
        {
            existing.Type = originalType;
            existing.Name = originalName;
            return null;
        }

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
        if (existing == null)
        {
            return false;
        }

        if (existing.Type == ProjectType.DefaultBuiltIn)
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

        var requestedIds = NormalizeRelationIds(mcpToolServerIds);
        var validIds =
            requestedIds.Count == 0
                ? []
                : (await _agentCatalog.FilterExistingMcpServerIdsAsync(requestedIds).ConfigureAwait(false)).ToList();
        if (validIds.Count != requestedIds.Count)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        var removedIds = currentIds.Except(validIds).ToList();
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

        foreach (var resourceId in validIds.Except(currentIds))
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

        var requestedIds = NormalizeRelationIds(skillIds);
        var validIds =
            requestedIds.Count == 0
                ? new HashSet<Guid>()
                : await _skillReferences.FilterVisibleSkillIdsAsync(requestedIds).ConfigureAwait(false);
        if (validIds.Count != requestedIds.Count)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        var removedIds = currentIds.Except(validIds).ToList();
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

        foreach (var resourceId in validIds.Except(currentIds))
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
        var relationIds = await _dbContext
            .ProjectConnectionRelations.Where(relation => relation.ProjectId == projectId)
            .Select(relation => relation.ConnectionId)
            .ToListAsync();
        var currentIds = await _connectionReferences.FilterOwnedConnectionIdsAsync(relationIds).ConfigureAwait(false);

        var requestedIds = NormalizeRelationIds(connectionIds);
        var validIds =
            requestedIds.Count == 0
                ? new HashSet<Guid>()
                : await _connectionReferences.FilterOwnedConnectionIdsAsync(requestedIds).ConfigureAwait(false);
        if (validIds.Count != requestedIds.Count)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        var removedIds = currentIds.Except(validIds).ToList();
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

        foreach (var resourceId in validIds.Except(currentIds))
        {
            await _dbContext.ProjectConnectionRelations.AddAsync(
                new ProjectConnectionRelation { ProjectId = projectId, ConnectionId = resourceId }
            );
        }
    }

    private async Task FilterVisibleReferenceRelationsAsync(IReadOnlyList<Project> projects)
    {
        var mcpToolServerIds = projects
            .SelectMany(project => project.ProjectMcpToolServers)
            .Select(relation => relation.McpToolServerId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var skillIds = projects
            .SelectMany(project => project.ProjectSkillRelations)
            .Select(relation => relation.SkillId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var connectionIds = projects
            .SelectMany(project => project.ProjectConnectionRelations)
            .Select(relation => relation.ConnectionId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        var visibleMcpToolServerIds =
            mcpToolServerIds.Length == 0
                ? new HashSet<Guid>()
                : await _agentCatalog.FilterExistingMcpServerIdsAsync(mcpToolServerIds).ConfigureAwait(false);
        var visibleSkillIds =
            skillIds.Length == 0
                ? new HashSet<Guid>()
                : await _skillReferences.FilterVisibleSkillIdsAsync(skillIds).ConfigureAwait(false);
        var visibleConnectionIds =
            connectionIds.Length == 0
                ? new HashSet<Guid>()
                : await _connectionReferences.FilterOwnedConnectionIdsAsync(connectionIds).ConfigureAwait(false);

        foreach (var project in projects)
        {
            project.ProjectMcpToolServers = project
                .ProjectMcpToolServers.Where(relation => visibleMcpToolServerIds.Contains(relation.McpToolServerId))
                .ToList();
            project.ProjectSkillRelations = project
                .ProjectSkillRelations.Where(relation => visibleSkillIds.Contains(relation.SkillId))
                .ToList();
            project.ProjectConnectionRelations = project
                .ProjectConnectionRelations.Where(relation => visibleConnectionIds.Contains(relation.ConnectionId))
                .ToList();
        }
    }

    private static IReadOnlyList<Guid> NormalizeRelationIds(IEnumerable<Guid>? relationIds)
    {
        return (relationIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
    }
}
