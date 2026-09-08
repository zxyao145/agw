using System.Text.Json;

namespace Agw.Agents.Execution.Agentflows.Checkpoints.Durable;

/// <summary>
/// 可跨 Server 持久化并恢复的 Agentflow JSON checkpoint。
/// </summary>
internal sealed record DurableAgentflowCheckpoint
{
    /// <summary>
    /// 获取 checkpoint 所属的 workflow session 标识。
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// 获取 checkpoint 标识。
    /// </summary>
    public required string CheckpointId { get; init; }

    /// <summary>
    /// 获取父 checkpoint 的 workflow session 标识。
    /// </summary>
    public string? ParentSessionId { get; init; }

    /// <summary>
    /// 获取父 checkpoint 标识。
    /// </summary>
    public string? ParentCheckpointId { get; init; }

    /// <summary>
    /// 获取 MAF 生成的 checkpoint JSON 内容。
    /// </summary>
    public required JsonElement Payload { get; init; }
}
