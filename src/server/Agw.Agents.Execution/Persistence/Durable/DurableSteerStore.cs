using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Steering;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Execution.Persistence.Durable;

/// <summary>
/// execution_steer 的读写。每个方法都在调用方锁定执行行的事务中运行：受理与终态交还因此互斥，
/// 终态提交之前受理的输入一定被送达或交还，之后的输入一定被拒绝。
/// Reads and writes execution_steer. Every method runs in the caller's transaction that locks the execution row: acceptance and the terminal hand-back exclude each other,
/// so an input accepted before the terminal commit is always delivered or handed back, and later inputs are always refused.
/// </summary>
internal sealed class DurableSteerStore
{
    private readonly IAgentsDbContext _dbContext;
    private readonly DurableExecutionStore _executions;

    public DurableSteerStore(IAgentsDbContext dbContext, DurableExecutionStore executions)
    {
        _dbContext = dbContext;
        _executions = executions;
    }

    /// <summary>
    /// 受理一条输入：Turn 必须属于该用户与对话、仍未结束且执行目标是 Agent；同一个 steerId 只受理一次。
    /// Accepts one input: the turn must belong to the user and conversation, not have finished, and target an Agent; one steerId is accepted once.
    /// </summary>
    public async Task OfferAsync(
        Guid turnId,
        string userId,
        Guid conversationId,
        SteerEntry entry,
        int maxPending,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(entry);
        var snapshot = await _executions.GetAuthorizedAsync(turnId, userId, cancellationToken).ConfigureAwait(false);
        if (
            snapshot.Manifest.Task.ProjectConversationId != conversationId
            || DurableExecutionQueries.IsTerminal(snapshot.Status)
        )
            throw new AgwException(ErrorCodes.SteerNoActiveTurn);
        if (snapshot.Manifest.AgentType != AgentRuntimeType.Agent)
            throw new AgwException(ErrorCodes.SteerNotSupported);

        var existing = await _dbContext
            .DurableExecutionSteers.AsNoTracking()
            .Where(steer => steer.Id == entry.SteerId)
            .Select(steer => (Guid?)steer.TurnId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing.HasValue)
        {
            if (existing.Value != turnId)
                throw new AgwException(ErrorCodes.InvalidParam, "The steerId is already used by another turn.");
            return;
        }

        var pending = await _dbContext
            .DurableExecutionSteers.CountAsync(
                steer => steer.TurnId == turnId && steer.Status == DurableExecutionSteerStatus.Pending,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (pending >= maxPending)
            throw new AgwException(ErrorCodes.SteerLimitExceeded);

        _dbContext.DurableExecutionSteers.Add(
            new DurableExecutionSteerRecord
            {
                Id = entry.SteerId,
                TurnId = turnId,
                Status = DurableExecutionSteerStatus.Pending,
                InputJson = DurableExecutionJson.Serialize(entry.Input),
            }
        );
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 取走 Turn 全部等待中的输入并记为 Delivered，按受理顺序返回。
    /// Takes all waiting inputs of the turn, records them as Delivered, and returns them in acceptance order.
    /// </summary>
    public Task<IReadOnlyList<SteerEntry>> TakePendingAsync(
        Guid turnId,
        int stepIndex,
        CancellationToken cancellationToken
    ) =>
        CompletePendingAsync(
            turnId,
            steer =>
            {
                steer.Status = DurableExecutionSteerStatus.Delivered;
                steer.StepIndex = stepIndex;
            },
            cancellationToken
        );

    /// <summary>
    /// Turn 结束时交还全部等待中的输入并记为 Returned，按受理顺序返回。
    /// Hands back all waiting inputs when the turn ends, records them as Returned, and returns them in acceptance order.
    /// </summary>
    public Task<IReadOnlyList<SteerEntry>> ReturnPendingAsync(
        Guid turnId,
        string reason,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return CompletePendingAsync(
            turnId,
            steer =>
            {
                steer.Status = DurableExecutionSteerStatus.Returned;
                steer.ReturnReason = reason;
            },
            cancellationToken
        );
    }

    private async Task<IReadOnlyList<SteerEntry>> CompletePendingAsync(
        Guid turnId,
        Action<DurableExecutionSteerRecord> complete,
        CancellationToken cancellationToken
    )
    {
        var pending = await _dbContext
            .DurableExecutionSteers.Where(steer =>
                steer.TurnId == turnId && steer.Status == DurableExecutionSteerStatus.Pending
            )
            .OrderBy(steer => steer.CreateTime)
            .ThenBy(steer => steer.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (pending.Count == 0)
            return [];
        foreach (var steer in pending)
            complete(steer);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return pending
            .Select(steer => new SteerEntry(
                steer.Id,
                DurableExecutionJson.DeserializeRequired<AgwUserInput>(steer.InputJson, "steering input")
            ))
            .ToArray();
    }
}
