namespace Agw.Agents.Contracts.Execution;

public sealed record AgentTurnSnapshot(Guid ProjectId, string UserId);

public interface ICurrentAgentTurn
{
    AgentTurnSnapshot? Current { get; }
}
