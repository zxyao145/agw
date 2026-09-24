using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Projects.Contracts;

public sealed record ConversationTurnListQuery
{
    [FromQuery(Name = "conversationId")]
    public Guid ConversationId { get; init; }

    /// <summary>
    /// 只返回 first_sequence 小于该值的 Turn；为空时从最近的 Turn 开始。
    /// Returns only turns whose first_sequence is below this value; empty starts from the latest turn.
    /// </summary>
    [FromQuery(Name = "beforeSequence")]
    public long? BeforeSequence { get; init; }

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
/// 按 first_sequence 倒序的一页 Turn；NextBeforeSequence 用作下一页的 beforeSequence。
/// One page of turns in descending first_sequence order; NextBeforeSequence is the beforeSequence of the next page.
/// </summary>
public sealed record ConversationTurnPageResponse(
    IReadOnlyList<ConversationTurnResponse> Items,
    long? NextBeforeSequence,
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
