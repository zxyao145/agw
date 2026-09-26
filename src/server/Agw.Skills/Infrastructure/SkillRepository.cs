using Agw.Shared.Contracts;
using Agw.Skills.Application.Persistence;
using Agw.Skills.Contracts;
using Agw.Skills.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Skills.Infrastructure;

public sealed class SkillRepository : ISkillRepository
{
    private readonly ISkillsDbContext _dbContext;
    private readonly ICurrentUser _currentUser;

    public SkillRepository(ISkillsDbContext dbContext, ICurrentUser currentUser)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
    }

    public Task<bool> ExistsVisibleWithNameAsync(string name, Guid excludedSkillId, CancellationToken cancellationToken)
    {
        var ownerUserId = _currentUser.RequiredUserId;
        return _dbContext.Skills.AnyAsync(
            skill =>
                skill.Name == name
                && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ownerUserId)
                && skill.Id != excludedSkillId,
            cancellationToken
        );
    }
}
