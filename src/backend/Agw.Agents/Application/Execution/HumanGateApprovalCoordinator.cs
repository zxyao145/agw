using System.Collections.Concurrent;

using Agw.Agents.Application.Agentflows;
using Agw.Agents.Contracts;

namespace Agw.Agents.Application.Execution;

public sealed class HumanGateApprovalCoordinator : IHumanGateApprovalHandler
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
    private readonly Action<HumanGateApprovalRequest?>? _pendingChanged;

    public HumanGateApprovalCoordinator(Action<HumanGateApprovalRequest?>? pendingChanged = null)
    {
        _pendingChanged = pendingChanged;
    }

    public async ValueTask<HumanGateApprovalDecision> WaitForApprovalAsync(
        HumanGateApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pending = _pending.GetOrAdd(
            request.RequestId,
            _ => new PendingApproval(
                request,
                new TaskCompletionSource<HumanGateApprovalDecision>(
                    TaskCreationOptions.RunContinuationsAsynchronously)));
        _pendingChanged?.Invoke(pending.Request);

        try
        {
            return await pending.Source.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (pending.Source.Task.IsCompleted || cancellationToken.IsCancellationRequested)
            {
                _pending.TryRemove(request.RequestId, out _);
                _pendingChanged?.Invoke(null);
            }
        }
    }

    public ValueTask<bool> TrySubmitAsync(
        HumanResponseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (cancellationToken.IsCancellationRequested ||
            string.IsNullOrWhiteSpace(command.RequestId) ||
            !_pending.TryRemove(command.RequestId, out var pending))
        {
            return ValueTask.FromResult(false);
        }

        var decision = new HumanGateApprovalDecision(
            command.RequestId,
            command.Approved,
            command.ResponseText);
        var submitted = pending.Source.TrySetResult(decision);
        if (submitted)
        {
            _pendingChanged?.Invoke(null);
        }

        return ValueTask.FromResult(submitted);
    }

    public void CancelAll()
    {
        foreach (var (requestId, pending) in _pending.ToArray())
        {
            if (_pending.TryRemove(requestId, out _))
            {
                pending.Source.TrySetCanceled();
            }
        }

        _pendingChanged?.Invoke(null);
    }

    private sealed record PendingApproval(
        HumanGateApprovalRequest Request,
        TaskCompletionSource<HumanGateApprovalDecision> Source);
}
