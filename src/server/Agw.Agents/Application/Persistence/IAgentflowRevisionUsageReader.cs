namespace Agw.Agents.Application.Persistence;

public interface IAgentflowRevisionUsageReader
{
    /// <summary>
    /// 当前用户的活跃 Durable 执行与可恢复 checkpoint 固定的全部 revision。
    /// Every revision pinned by the current user's active Durable executions and resumable checkpoints.
    /// </summary>
    Task<IReadOnlySet<(Guid AgentflowId, int Revision)>> ListPinnedRevisionsAsync(
        CancellationToken cancellationToken = default
    );
}
