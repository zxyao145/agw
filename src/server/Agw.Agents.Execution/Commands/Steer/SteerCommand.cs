using System.Text.Json.Serialization;
using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Steer;

/// <summary>
/// 向正在运行的 Turn 追加一条输入：在当前这批工具调用结束之后、下一次模型调用之前送达，不中断执行。
/// Adds an input to the running turn: it is delivered after the current batch of tool calls and before the next model call, without interrupting execution.
/// </summary>
public class SteerCommand : AgentRunCommand
{
    [JsonConstructor]
    public SteerCommand(Guid conversationId, Guid turnId, Guid steerId, AgwUserInput input)
    {
        ConversationId = conversationId;
        TurnId = turnId;
        SteerId = steerId;
        Input = input;
    }

    /// <summary>
    /// 必须等于连接已配置的对话。
    /// Must equal the conversation the connection is configured for.
    /// </summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// 客户端正在跟踪的 Turn；它不是活动 Turn 时命令被拒绝。
    /// The turn the client is tracking; the command is refused when it is not the active turn.
    /// </summary>
    public Guid TurnId { get; set; }

    /// <summary>
    /// 客户端生成的标识，同时是送达后用户消息的 messageId；重复提交只受理一次。
    /// The client-generated ID, also the messageId of the user message after delivery; a repeated submission is accepted once.
    /// </summary>
    public Guid SteerId { get; set; }

    public AgwUserInput Input { get; set; }
}
