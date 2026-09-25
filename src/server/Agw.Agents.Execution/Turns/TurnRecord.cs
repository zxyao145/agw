using Agw.Projects.Contracts.History;
using Agw.Shared.Contracts.Coordination;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Execution.Turns;

/// <summary>
/// 一个 Turn 的持久记录：受理时写入的输入行 ID，以及 Turn 行的状态推进、Step 数与结局。Durable 模式经写入入口在租约检查事务中提交。
/// The persisted record of one turn: the input row ID written at acceptance, and the turn row's status progress, Step count and outcome. Durable mode commits through the write guard inside lease-checked transactions.
/// </summary>
internal sealed class TurnRecord
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IExecutionWriteGuard? _writeGuard;

    public TurnRecord(
        IServiceScopeFactory scopeFactory,
        Guid turnId,
        Guid? inputMessageId,
        IExecutionWriteGuard? writeGuard
    )
    {
        _scopeFactory = scopeFactory;
        TurnId = turnId;
        InputMessageId = inputMessageId;
        _writeGuard = writeGuard;
    }

    public Guid TurnId { get; }

    /// <summary>
    /// 受理时已经写入的用户输入行；执行过程不再写入这条消息。
    /// The user input row written at acceptance; execution does not write this message again.
    /// </summary>
    public Guid? InputMessageId { get; }

    public Task MarkRunningAsync(CancellationToken cancellationToken) =>
        RunAsync((store, token) => store.MarkRunningAsync(TurnId, token), cancellationToken);

    public Task CompleteStepAsync(int stepCount, CancellationToken cancellationToken) =>
        RunAsync((store, token) => store.CompleteStepAsync(TurnId, stepCount, token), cancellationToken);

    public Task FinishAsync(
        ConversationTurnStatus status,
        int stepCount,
        string? errorCode,
        CancellationToken cancellationToken
    ) => RunAsync((store, token) => store.FinishAsync(TurnId, status, stepCount, errorCode, token), cancellationToken);

    /// <summary>
    /// 把结束消息的 status 映射为 Turn 行的终态。
    /// Maps the finish message status to the turn row's terminal status.
    /// </summary>
    public static ConversationTurnStatus ToTerminalStatus(string status) =>
        status switch
        {
            AgwTurnStatus.Completed => ConversationTurnStatus.Completed,
            AgwTurnStatus.Interrupted => ConversationTurnStatus.Interrupted,
            _ => ConversationTurnStatus.Failed,
        };

    private async Task RunAsync(
        Func<IConversationTurnStore, CancellationToken, Task> action,
        CancellationToken cancellationToken
    )
    {
        if (_writeGuard != null)
        {
            await _writeGuard
                .RunAsync(
                    async (services, token) =>
                    {
                        await action(services.GetRequiredService<IConversationTurnStore>(), token)
                            .ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<IConversationTurnStore>(), cancellationToken)
            .ConfigureAwait(false);
    }
}
