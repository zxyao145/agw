using System.Globalization;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Turns;

/// <summary>
/// 一个 Turn 的身份：开始与结束消息的字段来源。StreamingScopeId 是客户端输入消息的原始 ID，本 Turn 的输出显示在这条输入下。
/// The identity of one turn: the source of the start and finish message fields. StreamingScopeId is the client's original input message ID that the turn's output renders under.
/// </summary>
internal sealed record TurnEnvelope(
    Guid TurnId,
    Guid ConversationId,
    Guid AgentId,
    AgentRuntimeType AgentType,
    string StreamingScopeId
);

/// <summary>
/// 创建 Turn 生命周期控制消息，并定义 Turn 内消息携带的标识字段。
/// Creates turn lifecycle control messages and defines the identity fields carried by messages inside a turn.
/// </summary>
internal static class TurnMessageFactory
{
    public const string TurnIdKey = "turnId";
    public const string TurnSequenceKey = "turnSequence";
    public const string StepIndexKey = "stepIndex";

    public static AgwMessage CreateStarted(TurnEnvelope envelope) =>
        Create(AgwMessageTypes.TurnStart, CreateTurnProperties(envelope));

    public static AgwMessage CreateFinished(TurnEnvelope envelope, string status, int stepCount, string? errorCode)
    {
        var properties = CreateTurnProperties(envelope);
        properties[AgwMessageClassifier.StatusKey] = status;
        properties["stepCount"] = stepCount;
        if (!string.IsNullOrWhiteSpace(errorCode))
            properties["errorCode"] = errorCode;
        return Create(AgwMessageTypes.TurnFinished, properties);
    }

    /// <summary>
    /// 连接上没有进行中的 Turn 时，用结束消息让客户端退出等待状态。
    /// With no turn running on the connection, a finish message lets the client leave its waiting state.
    /// </summary>
    public static AgwMessage CreateFinished(string status) =>
        Create(
            AgwMessageTypes.TurnFinished,
            new AdditionalPropertiesDictionary { [AgwMessageClassifier.StatusKey] = status }
        );

    /// <summary>
    /// 结束消息的 errorCode：AgwException 取其七位错误码，其他异常归为执行失败。
    /// The errorCode of a finish message: an AgwException contributes its seven-digit code, other exceptions count as an execution failure.
    /// </summary>
    public static string? GetErrorCode(string status, Exception? failure) =>
        status != AgwTurnStatus.Failed ? null
        : failure is AgwException exception ? exception.Code.ToString(CultureInfo.InvariantCulture)
        : ErrorCodes.AgentExecutionFailed.Code.ToString(CultureInfo.InvariantCulture);

    private static AdditionalPropertiesDictionary CreateTurnProperties(TurnEnvelope envelope) =>
        new()
        {
            [TurnIdKey] = envelope.TurnId.ToString("D"),
            ["conversationId"] = envelope.ConversationId.ToString("D"),
            ["agentId"] = envelope.AgentId.ToString("D"),
            ["agentType"] = envelope.AgentType == AgentRuntimeType.Agentflow ? "agentflow" : "agent",
            ["streamingScopeId"] = envelope.StreamingScopeId,
        };

    private static AgwMessage Create(string type, AdditionalPropertiesDictionary properties)
    {
        properties[AgwMessageClassifier.TypeKey] = type;
        return new AgwMessage(
            Guid.CreateVersion7().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = "" }],
            properties
        );
    }
}
