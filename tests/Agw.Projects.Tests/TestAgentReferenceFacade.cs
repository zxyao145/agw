using Agw.Agents.Contracts.Catalog;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Repositories;

namespace Agw.Projects.Tests;

internal sealed class TestAgentReferenceFacade : IAgentReferenceFacade
{
    private readonly IRepository<Agent> _agents;
    private readonly IRepository<Agentflow> _agentflows;

    public TestAgentReferenceFacade(IRepository<Agent> agents, IRepository<Agentflow> agentflows)
    {
        _agents = agents;
        _agentflows = agentflows;
    }

    public async Task<bool> UsesAnyModelProviderAsync(
        IReadOnlyCollection<Guid> modelProviderIds,
        CancellationToken cancellationToken = default
    )
    {
        var ids = modelProviderIds.ToHashSet();
        return (
                await _agents.ListAsync(agent =>
                    agent.ModelProviderId.HasValue && ids.Contains(agent.ModelProviderId.Value)
                    || agent.SummaryModelProviderId.HasValue && ids.Contains(agent.SummaryModelProviderId.Value)
                )
            ).Count > 0
            || (
                await _agentflows.ListAsync(agentflow =>
                    agentflow.SummaryModelProviderId.HasValue && ids.Contains(agentflow.SummaryModelProviderId.Value)
                )
            ).Count > 0;
    }

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetAgentIdsBySkillIdsAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>>(new Dictionary<Guid, IReadOnlyList<Guid>>());

    public Task RemoveSkillBindingsAsync(Guid skillId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
