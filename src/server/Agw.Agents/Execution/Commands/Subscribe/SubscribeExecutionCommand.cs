using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Subscribe;

/// <summary>
/// 将当前 SignalR connection 重新附着到已有 durable execution，并从可选 cursor 继续消息回放。
/// </summary>
public sealed class SubscribeExecutionCommand : AgentRunCommand
{
    /// <summary>
    /// 创建 durable execution 重新订阅命令。
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    public SubscribeExecutionCommand(Guid executionId, string? cursor = null)
    {
        ExecutionId = executionId;
        Cursor = cursor;
    }

    /// <summary>
    /// 获取或设置需要重新附着的 durable execution。
    /// </summary>
    public Guid ExecutionId { get; set; }

    /// <summary>
    /// 获取或设置客户端最后确认的 Redis Stream cursor；为空时从头回放。
    /// </summary>
    public string? Cursor { get; set; }
}
