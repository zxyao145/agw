using Agw.Shared.Data.Entities.Executions;

namespace Agw.Agents.Application.Persistence;

public interface IAgentflowCheckpointPersistence
{
    Task<bool> RepairAndCheckActiveExecutionsAsync(
        Guid projectId,
        Guid conversationId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    );

    Task<TResult> ExecuteAsync<TResult>(
        Func<
            IAgentflowCheckpointPersistenceSession,
            CancellationToken,
            Task<AgentflowCheckpointPersistenceResult<TResult>>
        > operation,
        CancellationToken cancellationToken = default,
        Guid? conversationId = null,
        int expectedGeneration = 0
    );

    Task<AgentflowCheckpointRecord?> FindCheckpointAsync(
        Guid occurrenceId,
        CancellationToken cancellationToken = default
    );

    Task<bool> ProjectConversationExistsAsync(
        Guid projectId,
        Guid conversationId,
        string contextId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    );
}

public sealed record AgentflowCheckpointPersistenceResult<TResult>(TResult Result, bool Commit);

public interface IAgentflowCheckpointPersistenceSession
{
    IAgentsDbContext Agents { get; }

    Task<long> GetLastConversationSequenceAsync(Guid conversationId, CancellationToken cancellationToken = default);

    void AddConversationHistory(AgentflowCheckpointHistoryWrite history);

    /// <summary>
    /// 删除边界之后的历史与完全位于边界之后的 Turn，跨越边界的 Turn 把最大序号收回到边界。
    /// Deletes history after the boundary and turns lying entirely after it; a turn spanning the boundary has its largest sequence pulled back to the boundary.
    /// </summary>
    Task DeleteConversationHistoryAfterAsync(
        Guid conversationId,
        long boundarySequence,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// 为 Durable 恢复分支写入 Turn 行，目标是存档所属的 Agentflow。
    /// Writes the turn row of a durable resume branch whose target is the checkpoint's Agentflow.
    /// </summary>
    Task AcceptResumeTurnAsync(
        Guid turnId,
        AgentflowCheckpointRecord checkpoint,
        CancellationToken cancellationToken = default
    );
}

public sealed record AgentflowCheckpointHistoryWrite(
    Guid Id,
    Guid ConversationId,
    Guid TaskId,
    string? AgentName,
    long ConversationSequence,
    string ConversationPayload,
    DateTimeOffset Timestamp
)
{
    /// <summary>
    /// 写入存档标记的 Turn；标记是控制消息，没有 Step 序号。
    /// The turn that writes the checkpoint marker; markers are control messages without a Step index.
    /// </summary>
    public Guid? TurnId { get; init; }
}
