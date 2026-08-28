using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public class ProjectResolver
{
    private readonly IRepository<Project> _projectRepository;
    private readonly IUserInfoService _userInfoService;

    public ProjectResolver(IRepository<Project> projectRepository, IUserInfoService userInfoService)
    {
        _projectRepository = projectRepository;
        _userInfoService = userInfoService;
    }

    public Task<Project?> ResolveAsync(Guid? projectId, CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        var isDefaultProject = projectId == ProjectDefaults.DefaultBuiltInId;
        var isA2AProject = projectId == ProjectDefaults.A2AId;
        var resolvedProjectId = isDefaultProject || isA2AProject ? null : projectId;
        var defaultProjectName = isA2AProject ? ProjectDefaults.A2AName : ProjectDefaults.DefaultBuiltInName;
        return ResolveInternalAsync(resolvedProjectId, defaultProjectName, ownerUserId, cancellationToken);
    }

    public Task<Project?> ResolveA2AAsync(CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        return ResolveInternalAsync(null, ProjectDefaults.A2AName, ownerUserId, cancellationToken);
    }

    public Task<Project?> ResolveForUserAsync(
        Guid? projectId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            throw new AgwException(ErrorCodes.AuthenticationRequired);
        }

        var normalizedOwner = ownerUserId.Trim();
        if (!string.Equals(ResolveOwnerUserId(), normalizedOwner, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        var resolvedProjectId =
            !projectId.HasValue
            || projectId == Guid.Empty
            || projectId == ProjectDefaults.DefaultBuiltInId
            || projectId == ProjectDefaults.A2AId
                ? null
                : projectId;
        var defaultProjectName =
            projectId == ProjectDefaults.A2AId ? ProjectDefaults.A2AName : ProjectDefaults.DefaultBuiltInName;
        return ResolveInternalAsync(resolvedProjectId, defaultProjectName, normalizedOwner, cancellationToken);
    }

    public Task<Project?> ResolveA2AForUserAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            throw new AgwException(ErrorCodes.AuthenticationRequired);
        }

        var normalizedOwner = ownerUserId.Trim();
        if (!string.Equals(ResolveOwnerUserId(), normalizedOwner, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        return ResolveInternalAsync(null, ProjectDefaults.A2AName, normalizedOwner, cancellationToken);
    }

    public Task<Project?> ResolveRequiredAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        var defaultProjectName = projectId switch
        {
            var id when id == ProjectDefaults.DefaultBuiltInId => ProjectDefaults.DefaultBuiltInName,
            var id when id == ProjectDefaults.A2AId => ProjectDefaults.A2AName,
            _ => null,
        };
        var resolvedProjectId = defaultProjectName == null ? projectId : (Guid?)null;
        return ResolveInternalAsync(resolvedProjectId, defaultProjectName, ownerUserId, cancellationToken);
    }

    public async Task<Guid?> ResolveProjectIdAsync(Guid? projectId, CancellationToken cancellationToken = default)
    {
        var project = await ResolveAsync(projectId, cancellationToken);
        return project?.Id;
    }

    public async Task<Guid?> ResolveProjectIdForUserAsync(
        Guid? projectId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    )
    {
        var project = await ResolveForUserAsync(projectId, ownerUserId, cancellationToken).ConfigureAwait(false);
        return project?.Id;
    }

    private async Task<Project?> ResolveInternalAsync(
        Guid? projectId,
        string? defaultProjectName,
        string? ownerUserId,
        CancellationToken cancellationToken
    )
    {
        if (projectId == null && defaultProjectName == null)
        {
            return null;
        }

        if (projectId.HasValue)
        {
            return await _projectRepository.Queryable.FirstOrDefaultAsync(
                project => project.Id == projectId.Value && project.CreateBy == ownerUserId,
                cancellationToken
            );
        }

        return await _projectRepository.Queryable.FirstOrDefaultAsync(
            project =>
                project.Type == ProjectType.DefaultBuiltIn
                && project.Name == defaultProjectName
                && project.CreateBy == ownerUserId,
            cancellationToken
        );
    }

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;
}
