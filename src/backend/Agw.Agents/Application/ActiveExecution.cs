namespace Agw.Agents.Application;

/// <summary>
/// ActiveExecution 表示当前这一轮正在跑的命令执行句柄
/// </summary>
/// <param name="executionTask"></param>
/// <param name="cancellationTokenSource"></param>
/// <param name="session"></param>
public sealed class ActiveExecution(Task executionTask, CancellationTokenSource cancellationTokenSource, AgentExecSession? session = null)
    : IAsyncDisposable
{
    public Task ExecutionTask { get; } = executionTask;

    public bool InterruptRequested { get; private set; }

    private readonly CancellationTokenSource _cancellationTokenSource = cancellationTokenSource;

    /// <summary>
    /// AgentExecSession 表示连接级、可复用的 agent 会话上下文
    /// </summary>
    private readonly AgentExecSession? _session = session;

    public void RequestInterrupt(string? reason)
    {
        InterruptRequested = true;
        _session?.CancelActiveRequest();

        if (_cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ExecutionTask;
        }
        catch (Exception)
        {
        }
        _cancellationTokenSource.Dispose();
    }
}
