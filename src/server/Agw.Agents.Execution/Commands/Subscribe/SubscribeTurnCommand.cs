using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Subscribe;

/// <summary>
/// 将当前 SignalR connection 重新附着到已有 durable Turn，并从可选 cursor 继续消息回放。
/// </summary>
public sealed class SubscribeTurnCommand : AgentRunCommand
{
    /// <summary>
    /// 创建 durable Turn 重新订阅命令。
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    public SubscribeTurnCommand(Guid turnId, string? cursor = null)
    {
        TurnId = turnId;
        Cursor = cursor;
    }

    /// <summary>
    /// 获取或设置需要重新附着的 durable Turn。
    /// </summary>
    public Guid TurnId { get; set; }

    /// <summary>
    /// 获取或设置客户端最后确认的 Redis Stream cursor；为空时从头回放。
    /// </summary>
    public string? Cursor { get; set; }
}
