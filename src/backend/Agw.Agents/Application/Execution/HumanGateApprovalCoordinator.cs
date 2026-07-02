using System.Collections.Concurrent;

using Agw.Agents.Application.Agentflows;
using Agw.Agents.Contracts;

namespace Agw.Agents.Application.Execution;

public sealed class HumanGateApprovalCoordinator : IHumanGateApprovalHandler
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);

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

        try
        {
            return await pending.Source.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (pending.Source.Task.IsCompleted || cancellationToken.IsCancellationRequested)
            {
                _pending.TryRemove(request.RequestId, out _);
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
        return ValueTask.FromResult(pending.Source.TrySetResult(decision));
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
    }

    private sealed record PendingApproval(
        HumanGateApprovalRequest Request,
        TaskCompletionSource<HumanGateApprovalDecision> Source);
}
