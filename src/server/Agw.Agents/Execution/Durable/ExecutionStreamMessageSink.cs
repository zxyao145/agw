using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 把分段输出写入可回放事件流。事件流失败只损失实时回放，不能让 Agent 执行失败。
/// </summary>
internal sealed class ExecutionStreamMessageSink : IExecutionMessageSink
{
    private readonly IExecutionEventStream _stream;
    private readonly Guid _executionId;
    private readonly int _segmentIndex;
    private readonly ILogger _logger;
    private int _sequence = -1;
    private bool _disabled;

    /// <summary>
    /// 创建绑定到 execution 和 segment 的确定性消息 sink。
    /// </summary>
    public ExecutionStreamMessageSink(IExecutionEventStream stream, Guid executionId, int segmentIndex, ILogger logger)
    {
        _stream = stream;
        _executionId = executionId;
        _segmentIndex = segmentIndex;
        _logger = logger;
    }

    /// <summary>
    /// 以当前 segment 内递增 sequence 写入普通流式消息。
    /// </summary>
    public async ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
    {
        if (IsDeferredControlMessage(message))
        {
            // pending 和 terminal 必须在 PostgreSQL 状态落盘后由协调层发布，不能抢跑。
            return;
        }
        if (_disabled)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _sequence);
        await AppendBestEffortAsync(sequence, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 使用保留的最大 sequence 写入该 segment 的 terminal marker。
    /// </summary>
    public async ValueTask WriteTerminalAsync(string status, CancellationToken cancellationToken)
    {
        if (!_disabled)
        {
            await AppendBestEffortAsync(int.MaxValue, TurnMessageFactory.CreateFinished(status), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 尝试写入事件流；基础设施异常只禁用本分段 attempt 的后续输出。
    /// </summary>
    private async ValueTask AppendBestEffortAsync(int sequence, AgwMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await _stream
                .AppendAsync(_executionId, _segmentIndex, sequence, message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AgwException exception) when (exception.Code == ErrorCodes.DurableExecutionUnavailable.Code)
        {
            // 同一分段后续输出直接跳过，避免每个 token 都重复触发连接异常和日志。
            _disabled = true;
            _logger.LogWarning(
                exception,
                "Output replay is unavailable for distributed execution {ExecutionId} segment {SegmentIndex}; execution will continue without further stream output for this attempt.",
                _executionId,
                _segmentIndex
            );
        }
    }

    /// <summary>
    /// 判断控制消息是否必须延迟到 PostgreSQL 状态持久化后再发布。
    /// </summary>
    private static bool IsDeferredControlMessage(AgwMessage message)
    {
        if (
            message.AdditionalProperties == null
            || !message.AdditionalProperties.TryGetValue("type", out var value)
            || value is not string type
        )
        {
            return false;
        }

        return type
            is "human-interaction-request"
                or "tool-approval-request"
                or "human-gate-request"
                or TurnMessageProtocol.FinishedType;
    }
}
