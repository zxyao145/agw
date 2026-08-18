using System.Linq.Expressions;
using Agw.Files.Utils;
using Agw.Projects.Domain.Services;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public class ProjectAppService : IProjectAppService
{
    private readonly IRepository<Project> _projectRepository;
    private readonly IRepository<ProjectMcpServerRelation> _projectMcpToolServerRepository;
    private readonly IRepository<McpServer> _mcpToolServerRepository;
    private readonly IRepository<ProjectSkillRelation> _projectSkillRelationRepository;
    private readonly IRepository<Skill> _skillRepository;
    private readonly IRepository<ProjectConnectionRelation> _projectConnectionRelationRepository;
    private readonly IRepository<Connection> _connectionRepository;
    private readonly IRepository<AgentflowTrace> _traceRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ProjectDomainService _projectDomainService;
    private readonly ProjectResolver _projectResolver;

    public ProjectAppService(
        IRepository<Project> projectRepository,
        IRepository<ProjectMcpServerRelation> projectMcpToolServerRepository,
        IRepository<McpServer> mcpToolServerRepository,
        IRepository<ProjectSkillRelation> projectSkillRelationRepository,
        IRepository<Skill> skillRepository,
        IRepository<ProjectConnectionRelation> projectConnectionRelationRepository,
        IRepository<Connection> connectionRepository,
        IRepository<AgentflowTrace> traceRepository,
        IUnitOfWork unitOfWork,
        ProjectDomainService projectDomainService,
        ProjectResolver projectResolver
    )
    {
        _projectRepository = projectRepository;
        _projectMcpToolServerRepository = projectMcpToolServerRepository;
        _mcpToolServerRepository = mcpToolServerRepository;
        _projectSkillRelationRepository = projectSkillRelationRepository;
        _skillRepository = skillRepository;
        _projectConnectionRelationRepository = projectConnectionRelationRepository;
        _connectionRepository = connectionRepository;
        _traceRepository = traceRepository;
        _unitOfWork = unitOfWork;
        _projectDomainService = projectDomainService;
        _projectResolver = projectResolver;
    }

    public async Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null)
    {
        var projects = await _projectRepository.ListAsync(
            predicate,
            null,
            project => project.ProjectMcpToolServers,
            project => project.ProjectSkillRelations,
            project => project.ProjectConnectionRelations
        );
        return projects.OrderByDescending(project => project.CreateTime).ThenBy(project => project.Name).ToList();
    }

    public async Task<Project?> GetAsync(Guid id)
    {
        var projects = await _projectRepository.ListAsync(
            project => project.Id == id,
            null,
            project => project.ProjectMcpToolServers,
            project => project.ProjectSkillRelations,
            project => project.ProjectConnectionRelations
        );
        return projects.FirstOrDefault();
    }

    public Task<Project?> CreateAsync(Project project, string user) => CreateAsync(project, null, null, null, user);

    public async Task<Project?> CreateAsync(
        Project project,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? connectionIds,
        string user
    )
    {
        if (!_projectDomainService.TryPrepareForCreate(project, user))
        {
            return null;
        }

        EnsureWorkspaceDirectory(project.Workspace);
        await _projectRepository.AddAsync(project);
        await SyncProjectMcpToolServerRelationsAsync(project.Id, mcpToolServerIds);
        await SyncProjectSkillRelationsAsync(project.Id, skillIds);
        await SyncProjectConnectionRelationsAsync(project.Id, connectionIds);
        await _unitOfWork.SaveChangesAsync();
        return await GetAsync(project.Id);
    }

    public Task<Project?> UpdateAsync(Guid id, Action<Project> updateAction, string user) =>
        UpdateAsync(id, updateAction, null, null, null, user);

    public async Task<Project?> UpdateAsync(
        Guid id,
        Action<Project> updateAction,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? connectionIds,
        string user
    )
    {
        var existing = await _projectRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        if (!_projectDomainService.TryApplyUpdate(existing, updateAction, user))
        {
            return null;
        }

        EnsureWorkspaceDirectory(existing.Workspace);
        _projectRepository.Update(existing);
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
        await _unitOfWork.SaveChangesAsync();
        return await GetAsync(existing.Id);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await _projectRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        await _traceRepository.Queryable.Where(trace => trace.ProjectId == id).ExecuteDeleteAsync();
        _projectRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
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
        var currentIds = await _projectMcpToolServerRepository
            .Queryable.Where(relation => relation.ProjectId == projectId)
            .Select(relation => relation.McpToolServerId)
            .ToListAsync();

        var requestedIds = NormalizeRelationIds(mcpToolServerIds);
        var validIds =
            requestedIds.Count == 0
                ? []
                : (await _mcpToolServerRepository.ListAsync(server => requestedIds.Contains(server.Id)))
                    .Select(server => server.Id)
                    .ToList();
        var removedIds = currentIds.Except(validIds).ToList();
        if (removedIds.Count > 0)
        {
            var removedRelations = await _projectMcpToolServerRepository
                .Queryable.Where(relation =>
                    relation.ProjectId == projectId && removedIds.Contains(relation.McpToolServerId)
                )
                .ToListAsync();
            foreach (var relation in removedRelations)
            {
                _projectMcpToolServerRepository.Remove(relation);
            }
        }

        foreach (var resourceId in validIds.Except(currentIds))
        {
            await _projectMcpToolServerRepository.AddAsync(
                new ProjectMcpServerRelation { ProjectId = projectId, McpToolServerId = resourceId }
            );
        }
    }

    private async Task SyncProjectSkillRelationsAsync(Guid projectId, IEnumerable<Guid>? skillIds)
    {
        var currentIds = await _projectSkillRelationRepository
            .Queryable.Where(relation => relation.ProjectId == projectId)
            .Select(relation => relation.SkillId)
            .ToListAsync();

        var requestedIds = NormalizeRelationIds(skillIds);
        var validIds =
            requestedIds.Count == 0
                ? []
                : (await _skillRepository.ListAsync(skill => requestedIds.Contains(skill.Id)))
                    .Select(skill => skill.Id)
                    .ToList();
        var removedIds = currentIds.Except(validIds).ToList();
        if (removedIds.Count > 0)
        {
            var removedRelations = await _projectSkillRelationRepository
                .Queryable.Where(relation => relation.ProjectId == projectId && removedIds.Contains(relation.SkillId))
                .ToListAsync();
            foreach (var relation in removedRelations)
            {
                _projectSkillRelationRepository.Remove(relation);
            }
        }

        foreach (var resourceId in validIds.Except(currentIds))
        {
            await _projectSkillRelationRepository.AddAsync(
                new ProjectSkillRelation { ProjectId = projectId, SkillId = resourceId }
            );
        }
    }

    private async Task SyncProjectConnectionRelationsAsync(Guid projectId, IEnumerable<Guid>? connectionIds)
    {
        var currentIds = await _projectConnectionRelationRepository
            .Queryable.Where(relation => relation.ProjectId == projectId)
            .Select(relation => relation.ConnectionId)
            .ToListAsync();

        var requestedIds = NormalizeRelationIds(connectionIds);
        var validIds =
            requestedIds.Count == 0
                ? []
                : (await _connectionRepository.ListAsync(connection => requestedIds.Contains(connection.Id)))
                    .Select(connection => connection.Id)
                    .ToList();
        var removedIds = currentIds.Except(validIds).ToList();
        if (removedIds.Count > 0)
        {
            var removedRelations = await _projectConnectionRelationRepository
                .Queryable.Where(relation =>
                    relation.ProjectId == projectId && removedIds.Contains(relation.ConnectionId)
                )
                .ToListAsync();
            foreach (var relation in removedRelations)
            {
                _projectConnectionRelationRepository.Remove(relation);
            }
        }

        foreach (var resourceId in validIds.Except(currentIds))
        {
            await _projectConnectionRelationRepository.AddAsync(
                new ProjectConnectionRelation { ProjectId = projectId, ConnectionId = resourceId }
            );
        }
    }

    private static IReadOnlyList<Guid> NormalizeRelationIds(IEnumerable<Guid>? relationIds)
    {
        return (relationIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
    }
}
