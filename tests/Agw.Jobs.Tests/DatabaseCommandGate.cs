using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agw.Jobs.Tests;

internal sealed class DatabaseCommandGate : DbCommandInterceptor
{
    private readonly Func<DbCommand, bool> _matches;
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entered;

    public DatabaseCommandGate(Func<DbCommand, bool> matches, bool holdCleanup = false)
    {
        _matches = matches;
        if (holdCleanup is false)
        {
            _cleanup.SetResult();
        }
    }

    public TaskCompletionSource<CancellationToken> Entered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _release.TrySetResult();

    public void FinishCleanup() => _cleanup.TrySetResult();

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default
    )
    {
        if (_matches(command) && Interlocked.CompareExchange(ref _entered, 1, 0) == 0)
        {
            Entered.TrySetResult(cancellationToken);
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                CleanupStarted.TrySetResult();
                await _cleanup.Task;
            }
        }
        return result;
    }
}
