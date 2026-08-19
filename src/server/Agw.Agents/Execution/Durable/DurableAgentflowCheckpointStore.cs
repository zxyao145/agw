using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 单个 durable segment 内使用的 Agentflow checkpoint store。它本身不负责持久化；
/// 最新 checkpoint 会随分段结果写入 PostgreSQL，并注入下一分段。
/// </summary>
internal sealed class DurableAgentflowCheckpointStore : ICheckpointStore<JsonElement>
{
    private readonly List<DurableAgentflowCheckpoint> _checkpoints = [];

    /// <summary>
    /// 创建 checkpoint store，并可用上一个 segment result 中的 checkpoint 作为初始状态。
    /// </summary>
    public DurableAgentflowCheckpointStore(DurableAgentflowCheckpoint? checkpoint = null)
    {
        if (checkpoint != null)
        {
            _checkpoints.Add(checkpoint);
        }
    }

    /// <summary>
    /// 获取当前 segment 创建的最新 checkpoint。
    /// </summary>
    public DurableAgentflowCheckpoint? Latest => _checkpoints.LastOrDefault();

    /// <summary>
    /// 返回指定 workflow session 可用于恢复的 checkpoint 索引。
    /// </summary>
    public ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
        string sessionId,
        CheckpointInfo? withParent = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var checkpoints = _checkpoints
            .Where(item =>
                string.Equals(item.SessionId, sessionId, StringComparison.Ordinal)
                && (
                    withParent == null
                    || string.Equals(item.ParentSessionId, withParent.SessionId, StringComparison.Ordinal)
                        && string.Equals(item.ParentCheckpointId, withParent.CheckpointId, StringComparison.Ordinal)
                )
            )
            .Select(item => new CheckpointInfo(item.SessionId, item.CheckpointId))
            .ToArray();
        return ValueTask.FromResult<IEnumerable<CheckpointInfo>>(checkpoints);
    }

    /// <summary>
    /// 在当前 segment 内保存一个 JSON checkpoint，并返回其标识。
    /// </summary>
    public ValueTask<CheckpointInfo> CreateCheckpointAsync(
        string sessionId,
        JsonElement value,
        CheckpointInfo? parent = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var checkpointIdInput = string.Join(
            "\u001f",
            sessionId,
            _checkpoints.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            parent?.SessionId ?? string.Empty,
            parent?.CheckpointId ?? string.Empty,
            value.GetRawText()
        );
        var checkpointId = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkpointIdInput)))
            .ToLowerInvariant();
        var checkpointInfo = new CheckpointInfo(sessionId, checkpointId);
        _checkpoints.Add(
            new DurableAgentflowCheckpoint
            {
                SessionId = checkpointInfo.SessionId,
                CheckpointId = checkpointInfo.CheckpointId,
                ParentSessionId = parent?.SessionId,
                ParentCheckpointId = parent?.CheckpointId,
                Payload = value.Clone(),
            }
        );
        return ValueTask.FromResult(checkpointInfo);
    }

    /// <summary>
    /// 读取指定 checkpoint 的独立 JSON 副本。
    /// </summary>
    public ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(key);

        var checkpoint = _checkpoints.SingleOrDefault(item =>
            string.Equals(item.SessionId, sessionId, StringComparison.Ordinal)
            && string.Equals(item.CheckpointId, key.CheckpointId, StringComparison.Ordinal)
        );
        if (checkpoint == null)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                $"Checkpoint '{sessionId}/{key.CheckpointId}' was not found."
            );
        }

        return ValueTask.FromResult(checkpoint.Payload.Clone());
    }
}
