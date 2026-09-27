using Agw.Agents.Execution.Outbound;

namespace Agw.Agents.Tests;

/// <summary>
/// 在第一条结束消息处挂起写入，模拟客户端已经收到结束消息、服务端 Turn 任务尚未完成。
/// Holds the write at the first finish message, simulating a client that received it while the server turn task has not completed.
/// </summary>
internal sealed class FinishGateSink : IExecutionMessageSink
{
    private readonly object _lock = new();
    private readonly List<AgwMessage> _messages = [];
    private readonly TaskCompletionSource _firstFinish = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstFinish => _firstFinish.Task;

    public List<AgwMessage> Messages
    {
        get
        {
            lock (_lock)
            {
                return [.. _messages];
            }
        }
    }

    public void ReleaseFinish() => _release.TrySetResult();

    public async ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _messages.Add(message);
        }
        if (!AgwMessageClassifier.IsTurnFinished(message))
        {
            return;
        }

        _firstFinish.TrySetResult();
        await _release.Task;
    }
}
