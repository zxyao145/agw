using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;

/// <summary>
/// 保存在实际 Session 中的审批批次：Turn、节点、activation 与 Step 身份，完整原始请求，已收集的原生决定，以及等待期间随回答传入的普通消息。
/// The approval batch saved in the actual session: turn, node, activation and Step identity, the complete original requests, the collected native decisions, and ordinary messages that arrived with answers while waiting.
/// </summary>
internal sealed class MafApprovalBatchState
{
    public required string BatchId { get; init; }

    public required Guid TurnId { get; init; }

    public required string NodeId { get; init; }

    public required int ActivationIndex { get; init; }

    public required int StepIndex { get; init; }

    /// <summary>
    /// 按模型给出的顺序保存全部请求；自动批准的请求已有决定。
    /// Every request in model order; automatically approved requests already carry their decision.
    /// </summary>
    public required List<MafApprovalBatchItem> Items { get; init; }

    public List<ChatMessage> BufferedMessages { get; init; } = [];
}

/// <summary>
/// 批次中的一个原生请求。人工回答的结构化用户输入保存在决定的附加属性中。
/// One native request of the batch. The structured user input of a human answer is kept in the decision's additional properties.
/// </summary>
internal sealed class MafApprovalBatchItem
{
    public required ToolApprovalRequestContent Request { get; init; }

    public required bool RequiresHuman { get; init; }

    public ToolApprovalResponseContent? Response { get; set; }
}
