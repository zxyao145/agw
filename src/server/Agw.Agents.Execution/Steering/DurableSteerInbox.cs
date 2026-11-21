using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Shared.Contracts.Coordination;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Execution.Steering;

/// <summary>
/// Distributed execution 一个 Segment 读取 Turn 待送达输入的入口：取走输入在租约检查事务中把它们记为 Delivered，
/// 失去租约的实例因此不能再取走。之后到达的输入由终态提交在锁定执行行的事务中交还。
/// The access of one Distributed execution segment to the turn's waiting inputs: taking them marks them Delivered in a lease-checked transaction,
/// so an instance that lost its lease can no longer take them. Inputs arriving later are handed back by the terminal commit in the transaction that locks the execution row.
/// </summary>
internal sealed class DurableSteerInbox : ISteerInbox
{
    private readonly IExecutionWriteGuard _guard;
    private readonly Guid _turnId;

    public DurableSteerInbox(IExecutionWriteGuard guard, Guid turnId)
    {
        ArgumentNullException.ThrowIfNull(guard);
        _guard = guard;
        _turnId = turnId;
    }

    public async ValueTask<IReadOnlyList<SteerEntry>> TakePendingAsync(CancellationToken cancellationToken)
    {
        var stepIndex = ExecutionScope.Current?.StepIndex ?? 0;
        return await _guard
            .RunAsync(
                (services, token) =>
                    services.GetRequiredService<DurableSteerStore>().TakePendingAsync(_turnId, stepIndex, token),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<SteerEntry>> TakePendingOrCloseAsync(CancellationToken cancellationToken) =>
        TakePendingAsync(cancellationToken);
}
