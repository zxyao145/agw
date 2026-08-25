using Agw.Projects.Contracts.Execution;

namespace Agw.Projects.Application.Facades;

public sealed class ProjectProviderSessionFacade : IProjectProviderSessionFacade
{
    private readonly ITaskSessionBindingService _bindings;

    public ProjectProviderSessionFacade(ITaskSessionBindingService bindings)
    {
        _bindings = bindings;
    }

    public async Task<string?> GetProviderSessionIdAsync(
        ProjectProviderSessionReference reference,
        CancellationToken cancellationToken = default
    )
    {
        var binding = await _bindings
            .GetAsync(
                reference.ProjectId,
                reference.ContextId,
                reference.AgentId,
                reference.ExternalAgentName,
                cancellationToken
            )
            .ConfigureAwait(false);
        return binding?.ProviderSessionId;
    }

    public async Task SaveProviderSessionIdAsync(
        ProjectProviderSessionReference reference,
        string providerSessionId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    )
    {
        await _bindings
            .UpsertAsync(
                reference.ProjectId,
                reference.ContextId,
                reference.AgentId,
                reference.ExternalAgentName,
                providerSessionId,
                ownerUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
    }
}
