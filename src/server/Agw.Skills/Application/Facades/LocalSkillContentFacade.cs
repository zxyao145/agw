using Agw.Skills.Contracts;

namespace Agw.Skills.Application.Facades;

public sealed class LocalSkillContentFacade : ILocalSkillContentResolver
{
    private readonly SkillAppService _skillAppService;

    public LocalSkillContentFacade(SkillAppService skillAppService)
    {
        _skillAppService = skillAppService;
    }

    public Task<ILocalSkillDirectoryLease?> AcquireDirectoryAsync(
        Guid skillId,
        CancellationToken cancellationToken = default
    ) => _skillAppService.AcquireLocalDirectoryAsync(skillId, cancellationToken);
}
