namespace Agw.Agents.Application.Persistence;

public interface IAgentflowRevisionSnapshotReader
{
    Task<AgentflowRevisionSnapshot> CreateRevisionSnapshotAsync(
        Guid agentflowId,
        int requestedRevision,
        CancellationToken cancellationToken = default
    );

    Task<bool> DefinitionsRemainAvailableAsync(
        Guid rootAgentflowId,
        AgentflowRevisionSnapshot snapshot,
        CancellationToken cancellationToken = default
    );
}
