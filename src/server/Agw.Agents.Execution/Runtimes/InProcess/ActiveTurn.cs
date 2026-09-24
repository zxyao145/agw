using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Runtimes.InProcess;

/// <summary>
/// The running execution turn and its cancellation source.
/// </summary>
internal sealed class ActiveTurn : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Action? _interruptAction;
    private readonly Func<InteractionResponse, CancellationToken, ValueTask<bool>>? _submitHumanResponseAsync;
    private readonly Lock _sync = new();
    private bool _disposed;

    public ActiveTurn(
        Task executionTask,
        CancellationTokenSource cancellationTokenSource,
        Action? interruptAction = null,
        Func<InteractionResponse, CancellationToken, ValueTask<bool>>? submitHumanResponseAsync = null
    )
    {
        ExecutionTask =
            executionTask ?? throw new AgwException(ErrorCodes.InvalidParam, "executionTask cannot be null.");
        _cancellationTokenSource =
            cancellationTokenSource
            ?? throw new AgwException(ErrorCodes.InvalidParam, "cancellationTokenSource cannot be null.");
        _interruptAction = interruptAction;
        _submitHumanResponseAsync = submitHumanResponseAsync;
    }

    public Task ExecutionTask { get; }

    public bool IsCompleted => ExecutionTask.IsCompleted;

    public void RequestInterrupt()
    {
        // Some runtimes need an explicit interruption hook in addition to cancellation.
        _interruptAction?.Invoke();

        // 中断钩子可能让回合当场结束并释放本对象；取消与释放共用一把锁。
        // The interrupt hook can finish and dispose the turn inline; cancellation and disposal share one lock.
        lock (_sync)
        {
            if (_disposed || _cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            _cancellationTokenSource.Cancel();
        }
    }

    public ValueTask<bool> TrySubmitHumanResponseAsync(InteractionResponse command, CancellationToken cancellationToken)
    {
        return _submitHumanResponseAsync?.Invoke(command, cancellationToken) ?? ValueTask.FromResult(false);
    }

    public async ValueTask DisposeAsync()
    {
        // Wait for the task to settle before releasing the linked cancellation source.
        await ExecutionTask.ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding
        );

        lock (_sync)
        {
            _disposed = true;
            _cancellationTokenSource.Dispose();
        }
    }
}
