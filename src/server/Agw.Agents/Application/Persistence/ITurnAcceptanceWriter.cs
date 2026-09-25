using Agw.Projects.Contracts.History;
using Agw.Shared.Data.Entities.Executions;

namespace Agw.Agents.Application.Persistence;

/// <summary>
/// 受理事务：用户输入行与 Turn 行属于 Projects，Durable 执行记录与序号 1 的开始事件属于 Agents，三者在同一个事务中提交。
/// The acceptance transaction: the user input row and turn row belong to Projects, the durable execution record and the start event with sequence 1 belong to Agents, and all commit in one transaction.
/// </summary>
public interface ITurnAcceptanceWriter
{
    /// <summary>
    /// turnId 幂等：同一用户在同一对话重发时返回已有记录；turnId 属于其他用户或其他对话时与对话不存在报同样的错误。
    /// Idempotent by turnId: a resend by the same user in the same conversation returns the existing records; a turnId of another user or conversation fails like a missing conversation.
    /// </summary>
    Task<TurnAcceptanceResult> AcceptAsync(TurnAcceptanceWrite write, CancellationToken cancellationToken);
}

public sealed record TurnAcceptanceWrite
{
    public required AcceptConversationTurnRequest Turn { get; init; }

    public DurableTurnRegistration? Durable { get; init; }
}

/// <summary>
/// Durable 登记：WorkerId 不为空时受理实例立即在本地执行，记录写为 Running 并带租约；为空时写为 Queued，由 Worker 领取。
/// The durable registration: with a WorkerId the accepting instance runs locally at once and the record is Running with a lease; without one it is Queued for a worker to claim.
/// </summary>
public sealed record DurableTurnRegistration
{
    public required string UserId { get; init; }

    public required string ManifestJson { get; init; }

    public string? WorkerId { get; init; }

    public required TimeSpan LeaseDuration { get; init; }

    public required Guid StartEventId { get; init; }

    public required string StartEventPayloadJson { get; init; }
}

public sealed record TurnAcceptanceResult(ConversationTurnSnapshot Turn, bool Created, DurableTurnAcceptance? Durable);

public sealed record DurableTurnAcceptance(DurableExecutionStatus Status, DurableLease? Lease);

/// <summary>
/// 租约持有者：WorkerId 与 LeaseEpoch 共同确定，每次领取 LeaseEpoch 加一。
/// The lease holder, identified by WorkerId and LeaseEpoch together; every claim increments LeaseEpoch.
/// </summary>
public sealed record DurableLease(Guid ExecutionId, string WorkerId, long Epoch);
