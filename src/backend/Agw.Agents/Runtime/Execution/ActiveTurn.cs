using Agw.Agents.Runtime.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Runtime.Execution;

/// <summary>
/// The running execution turn and its cancellation source.
/// </summary>
/// <param name="executionTask"></param>
/// <param name="cancellationTokenSource"></param>
/// <param name="interruptAction"></param>
/// <param name="submitHumanResponseAsync"></param>
public sealed class ActiveTurn(
    Task executionTask,
    CancellationTokenSource cancellationTokenSource,
    Action? interruptAction = null,
    Func<HumanResponseCommand, CancellationToken, ValueTask<bool>>? submitHumanResponseAsync = null) : IAsyncDisposable
{
    public Task ExecutionTask { get; } = executionTask ?? throw new AgwException(ErrorCodes.InvalidParam, "executionTask cannot be null.");

    public bool InterruptRequested { get; private set; }

    public bool IsCompleted => ExecutionTask.IsCompleted;

    private readonly CancellationTokenSource _cancellationTokenSource =
        cancellationTokenSource ?? throw new AgwException(ErrorCodes.InvalidParam, "cancellationTokenSource cannot be null.");

    private readonly Action? _interruptAction = interruptAction;

    private readonly Func<HumanResponseCommand, CancellationToken, ValueTask<bool>>? _submitHumanResponseAsync =
        submitHumanResponseAsync;

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

    public ValueTask<bool> TrySubmitHumanResponseAsync(
        HumanResponseCommand command,
        CancellationToken cancellationToken)
    {
        return _submitHumanResponseAsync?.Invoke(command, cancellationToken) ?? ValueTask.FromResult(false);
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
