using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;

using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Application;

public class ModelProviderUsageGuard
{
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<Agentflow> _agentflowRepository;

    public ModelProviderUsageGuard(
        IRepository<Agent> agentRepository,
        IRepository<Agentflow> agentflowRepository)
    {
        _agentRepository = agentRepository;
        _agentflowRepository = agentflowRepository;
    }

    public async Task EnsureNotInUseAsync(IEnumerable<Guid> modelProviderIds)
    {
        var ids = modelProviderIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var usedByAgent = await _agentRepository.Queryable.AnyAsync(agent =>
            (agent.ModelProviderId.HasValue && ids.Contains(agent.ModelProviderId.Value)) ||
            (agent.SummaryModelProviderId.HasValue && ids.Contains(agent.SummaryModelProviderId.Value)));
        var usedByAgentflow = await _agentflowRepository.Queryable.AnyAsync(agentflow =>
            agentflow.SummaryModelProviderId.HasValue && ids.Contains(agentflow.SummaryModelProviderId.Value));
        if (usedByAgent || usedByAgentflow)
        {
            throw new AgwException(ErrorCodes.ModelProviderInUse);
        }
    }
}
