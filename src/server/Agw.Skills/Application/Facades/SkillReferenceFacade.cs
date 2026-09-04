using Agw.Shared.Contracts;
using Agw.Shared.Data.Entities.Skills;
using Agw.Skills.Application.Persistence;
using Agw.Skills.Contracts.References;
using Microsoft.EntityFrameworkCore;

namespace Agw.Skills.Application.Facades;

public sealed class SkillReferenceFacade : ISkillReferenceFacade
{
    private readonly ISkillsDbContext _dbContext;
    private readonly ICurrentUser _currentUser;

    public SkillReferenceFacade(ISkillsDbContext dbContext, ICurrentUser currentUser)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlySet<Guid>> FilterVisibleSkillIdsAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken = default
    )
    {
        var ids = NormalizeIds(skillIds);
        if (ids.Length == 0)
        {
            return new HashSet<Guid>();
        }

        var ownerUserId = _currentUser.RequiredUserId;
        return await _dbContext
            .Skills.AsNoTracking()
            .Where(skill =>
                ids.Contains(skill.Id) && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ownerUserId)
            )
            .Select(skill => skill.Id)
            .ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SkillReferenceSnapshot>> ResolveVisibleSkillsAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken = default
    )
    {
        var ids = NormalizeIds(skillIds);
        if (ids.Length == 0)
        {
            return [];
        }

        var ownerUserId = _currentUser.RequiredUserId;
        return await _dbContext
            .Skills.AsNoTracking()
            .Where(skill =>
                ids.Contains(skill.Id) && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ownerUserId)
            )
            .Select(skill => new SkillReferenceSnapshot(
                skill.Id,
                skill.Name,
                skill.Description,
                skill.Kind,
                skill.ContentPath
            ))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SkillDescriptor>> DescribeVisibleSkillsAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken = default
    )
    {
        var ids = NormalizeIds(skillIds);
        if (ids.Length == 0)
        {
            return [];
        }

        var ownerUserId = _currentUser.RequiredUserId;
        return await _dbContext
            .Skills.AsNoTracking()
            .Where(skill =>
                ids.Contains(skill.Id) && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ownerUserId)
            )
            .Select(skill => new SkillDescriptor(skill.Id, skill.Name, skill.Description))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static Guid[] NormalizeIds(IReadOnlyCollection<Guid> skillIds)
    {
        ArgumentNullException.ThrowIfNull(skillIds);
        return skillIds.Where(id => id != Guid.Empty).Distinct().ToArray();
    }
}
