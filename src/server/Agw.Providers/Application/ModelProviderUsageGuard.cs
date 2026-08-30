using Agw.Agents.Contracts.Catalog;
using Agw.Shared.Exceptions;

namespace Agw.Providers.Application;

public class ModelProviderUsageGuard
{
    private readonly IAgentReferenceFacade _agentReferences;

    public ModelProviderUsageGuard(IAgentReferenceFacade agentReferences)
    {
        _agentReferences = agentReferences;
    }

    public async Task EnsureNotInUseAsync(IEnumerable<Guid> modelProviderIds)
    {
        var ids = modelProviderIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        if (await _agentReferences.UsesAnyModelProviderAsync(ids).ConfigureAwait(false))
        {
            throw new AgwException(ErrorCodes.ModelProviderInUse);
        }
    }
}
