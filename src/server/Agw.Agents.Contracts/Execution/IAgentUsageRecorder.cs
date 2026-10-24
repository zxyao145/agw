namespace Agw.Agents.Contracts.Execution;

public interface IAgentUsageRecorder
{
    /// <summary>
    /// 写入一条用量；turnId 是写入时所在的 Turn，不在 Turn 中执行时为空。
    /// Writes one usage record; turnId is the turn it was written in, or null outside a turn.
    /// </summary>
    Task AddAsync(
        Guid projectId,
        string contextId,
        string agentName,
        Guid? turnId,
        ProjectContextUsage usage,
        CancellationToken cancellationToken = default
    );
}
