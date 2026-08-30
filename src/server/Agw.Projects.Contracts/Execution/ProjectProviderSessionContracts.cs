namespace Agw.Projects.Contracts.Execution;

public sealed record ProjectProviderSessionReference(
    Guid ProjectId,
    string ContextId,
    Guid AgentId,
    string ExternalAgentName
);

public interface IProjectProviderSessionFacade
{
    Task<string?> GetProviderSessionIdAsync(
        ProjectProviderSessionReference reference,
        CancellationToken cancellationToken = default
    );

    Task SaveProviderSessionIdAsync(
        ProjectProviderSessionReference reference,
        string providerSessionId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    );
}
