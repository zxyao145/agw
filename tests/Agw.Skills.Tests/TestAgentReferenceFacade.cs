using Agw.Agents.Contracts.Catalog;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Repositories;

namespace Agw.Skills.Tests;

internal sealed class TestAgentReferenceFacade : IAgentReferenceFacade
{
    private readonly IRepository<AgentSkillRelation> _relations;
    private readonly IUnitOfWork _unitOfWork;

    public TestAgentReferenceFacade(IRepository<AgentSkillRelation> relations, IUnitOfWork unitOfWork)
    {
        _relations = relations;
        _unitOfWork = unitOfWork;
    }

    public Task<bool> UsesAnyModelProviderAsync(
        IReadOnlyCollection<Guid> modelProviderIds,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(false);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetAgentIdsBySkillIdsAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken = default
    )
    {
        var ids = skillIds.ToHashSet();
        var relations = await _relations.ListAsync(relation => ids.Contains(relation.SkillId));
        return relations
            .GroupBy(relation => relation.SkillId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Guid>)group.Select(relation => relation.AgentId).ToArray()
            );
    }

    public async Task RemoveSkillBindingsAsync(Guid skillId, CancellationToken cancellationToken = default)
    {
        var relations = await _relations.ListAsync(relation => relation.SkillId == skillId);
        foreach (var relation in relations)
        {
            _relations.Remove(relation);
        }
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
