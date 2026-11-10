using System.Globalization;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Extensions;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Steering;

/// <summary>
/// 创建 Steer 的下发消息：送达时的用户消息与 steer-delivered，未送达时携带原始输入的 steer-returned。
/// Creates the outbound Steer messages: the user message and steer-delivered on delivery, and steer-returned carrying the original input when not delivered.
/// </summary>
internal static class SteerMessages
{
    public const string SteerIdKey = "steerId";
    public const string ReasonKey = "reason";

    /// <summary>
    /// Steer 的用户消息：messageId 等于 steerId，属于被 Steer 的 Turn，客户端据此把它放在转录中的实际位置。
    /// The user message of a Steer: its messageId equals the steerId and it belongs to the steered turn, so clients place it at its actual position in the transcript.
    /// </summary>
    public static AgwMessage CreateUserMessage(ExecutionScope scope, SteerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entry);
        var properties = CreateTurnProperties(scope);
        properties[SteerIdKey] = entry.SteerId.ToString("D");
        return new AgwMessage(
            entry.SteerId.ToString("D"),
            entry.Input.Author,
            AiRole.User,
            [.. entry.Input.Contents],
            properties
        )
        {
            CreatedAt = entry.Input.CreatedAt,
        };
    }

    public static AgwMessage CreateDelivered(ExecutionScope scope, SteerEntry entry, int stepIndex)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entry);
        var properties = CreateTurnProperties(scope);
        properties[AgwMessageClassifier.TypeKey] = AgwMessageTypes.SteerDelivered;
        properties[SteerIdKey] = entry.SteerId.ToString("D");
        properties[TurnMessageFactory.StepIndexKey] = stepIndex;
        return CreateControl(properties);
    }

    /// <summary>
    /// 未送达的 Steer：消息内容是原始输入，客户端据此恢复文字与附件。
    /// A Steer that was not delivered: the message contents are the original input, from which clients restore the text and attachments.
    /// </summary>
    public static AgwMessage CreateReturned(ExecutionScope scope, SteerEntry entry, string reason)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return CreateReturned(CreateTurnProperties(scope), entry, reason);
    }

    /// <summary>
    /// 没有执行作用域时（Distributed 的终态提交）按 Turn 身份创建 steer-returned。
    /// Creates steer-returned from the turn identity when there is no execution scope (the Distributed terminal commit).
    /// </summary>
    public static AgwMessage CreateReturned(TurnEnvelope envelope, SteerEntry entry, string reason)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return CreateReturned(
            CreateTurnProperties(envelope.TurnId, envelope.ConversationId, envelope.StreamingScopeId),
            entry,
            reason
        );
    }

    private static AgwMessage CreateReturned(AdditionalPropertiesDictionary properties, SteerEntry entry, string reason)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        properties[AgwMessageClassifier.TypeKey] = AgwMessageTypes.SteerReturned;
        properties[SteerIdKey] = entry.SteerId.ToString("D");
        properties[ReasonKey] = reason;
        return new AgwMessage(
            Guid.CreateVersion7().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [.. entry.Input.Contents],
            properties
        );
    }

    private static AgwMessage CreateControl(AdditionalPropertiesDictionary properties) =>
        new(
            Guid.CreateVersion7().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = "" }],
            properties
        );

    private static AdditionalPropertiesDictionary CreateTurnProperties(ExecutionScope scope) =>
        CreateTurnProperties(scope.Context.TurnId, scope.ProjectConversationId, scope.Context.TurnId.ToString("D"));

    private static AdditionalPropertiesDictionary CreateTurnProperties(
        Guid turnId,
        Guid conversationId,
        string streamingScopeId
    ) =>
        new()
        {
            [TurnMessageFactory.TurnIdKey] = turnId.ToString("D"),
            ["conversationId"] = conversationId.ToString("D"),
            ["streamingScopeId"] = streamingScopeId,
        };

    /// <summary>
    /// 送达原因文本使用的 Turn 结局到退回原因的映射。
    /// Maps a turn status to the return reason text.
    /// </summary>
    public static string ToReturnReason(string turnStatus) =>
        string.Create(CultureInfo.InvariantCulture, $"turn-{turnStatus}");
}
