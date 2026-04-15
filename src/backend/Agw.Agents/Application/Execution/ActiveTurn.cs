using Agw.Shared.Exceptions;

namespace Agw.Agents.Application.Execution;

/// <summary>
/// The running execution turn and its cancellation source.
/// </summary>
/// <param name="executionTask"></param>
/// <param name="cancellationTokenSource"></param>
/// <param name="interruptAction"></param>
public sealed class ActiveTurn(
    Task executionTask,
    CancellationTokenSource cancellationTokenSource,
    Action? interruptAction = null) : IAsyncDisposable
{
    public Task ExecutionTask { get; } = executionTask ?? throw new AgwException(ErrorCodes.InvalidParam, "executionTask cannot be null.");

    public bool InterruptRequested { get; private set; }

    public bool IsCompleted => ExecutionTask.IsCompleted;

    private readonly CancellationTokenSource _cancellationTokenSource =
        cancellationTokenSource ?? throw new AgwException(ErrorCodes.InvalidParam, "cancellationTokenSource cannot be null.");

    private readonly Action? _interruptAction = interruptAction;

    public void RequestInterrupt(string? reason)
    {
        InterruptRequested = true;
        // Some runtimes need an explicit interruption hook in addition to cancellation.
        _interruptAction?.Invoke();

        if (_cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        // Wait for the task to settle before releasing the linked cancellation source.
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
