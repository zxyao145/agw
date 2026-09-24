using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Projects.Contracts;

public sealed record ConversationTurnListQuery
{
    [FromQuery(Name = "conversationId")]
    public Guid ConversationId { get; init; }

    /// <summary>
    /// 与 BeforeTurnId 一起组成游标，取自上一页的 NextBeforeSequence；只返回排在该游标之后的 Turn。两者都为空时从最近的 Turn 开始。
    /// Forms the cursor together with BeforeTurnId, taken from the previous page's NextBeforeSequence; only turns ordered after the cursor are returned. Both empty starts from the latest turn.
    /// </summary>
    [FromQuery(Name = "beforeSequence")]
    public long? BeforeSequence { get; init; }

    /// <summary>
    /// 游标中的 Turn ID，取自上一页的 NextBeforeTurnId；必须与 BeforeSequence 同时提供。
    /// The turn ID of the cursor, taken from the previous page's NextBeforeTurnId; must be provided together with BeforeSequence.
    /// </summary>
    [FromQuery(Name = "beforeTurnId")]
    public Guid? BeforeTurnId { get; init; }

    [FromQuery(Name = "limit")]
    [Range(1, 100)]
    public int Limit { get; init; } = 20;
}

public sealed record ConversationTurnMessagesQuery
{
    [FromQuery(Name = "conversationId")]
    public Guid ConversationId { get; init; }

    [FromQuery(Name = "turnId")]
    public Guid TurnId { get; init; }
}

/// <summary>
/// 一个 Turn 的摘要。Status 取 accepted / running / completed / failed / interrupted，AgentType 取 agent / agentflow。
/// The summary of one turn. Status is accepted / running / completed / failed / interrupted, AgentType is agent / agentflow.
/// </summary>
public sealed record ConversationTurnResponse(
    Guid TurnId,
    Guid ConversationId,
    string Status,
    Guid AgentId,
    string AgentType,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int StepCount,
    long FirstSequence,
    long? LastSequence,
    string? ErrorCode,
    Guid InputMessageId,
    string InputSummary
);

/// <summary>
/// 按 first_sequence 倒序、同一序号内按 turnId 倒序的一页 Turn；NextBeforeSequence 与 NextBeforeTurnId 分别用作下一页的 beforeSequence 与 beforeTurnId。
/// One page of turns in descending first_sequence order, then descending turnId within one sequence; NextBeforeSequence and NextBeforeTurnId are the beforeSequence and beforeTurnId of the next page.
/// </summary>
public sealed record ConversationTurnPageResponse(
    IReadOnlyList<ConversationTurnResponse> Items,
    long? NextBeforeSequence,
    Guid? NextBeforeTurnId,
    bool HasMore
);

/// <summary>
/// Turn 内的一条消息。StepIndex 只用于同一节点内分组，排序使用 Sequence。
/// One message of a turn. StepIndex only groups messages within one node; ordering uses Sequence.
/// </summary>
public sealed record ConversationTurnMessageResponse(long Sequence, int? StepIndex, AgwMessage Message);

public sealed record ConversationTurnMessagesResponse(
    Guid TurnId,
    IReadOnlyList<ConversationTurnMessageResponse> Items
);
