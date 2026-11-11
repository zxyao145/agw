using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Agents;

public sealed class BackgroundExecutionPersistence : IBackgroundExecutionPersistence
{
    private readonly AgwDbContext _dbContext;

    public BackgroundExecutionPersistence(AgwDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<BackgroundExecution?> FindAsync(Guid turnId, string ownerUserId, CancellationToken cancellationToken) =>
        _dbContext
            .BackgroundExecutions.AsNoTracking()
            .SingleOrDefaultAsync(
                execution => execution.Id == turnId && execution.CreateBy == ownerUserId,
                cancellationToken
            );

    public void Stage(BackgroundExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        _dbContext.BackgroundExecutions.Add(execution);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        _dbContext.SaveChangesAsync(cancellationToken);

    public async Task<bool> MarkRunningAsync(
        Guid turnId,
        string ownerUserId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken
    ) =>
        await _dbContext
            .BackgroundExecutions.Where(execution =>
                execution.Id == turnId
                && execution.CreateBy == ownerUserId
                && execution.State == Agw.Agents.Contracts.Execution.AgentExecutionState.Queued
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(
                            execution => execution.State,
                            Agw.Agents.Contracts.Execution.AgentExecutionState.Running
                        )
                        .SetProperty(execution => execution.StartedAt, startedAt)
                        .SetProperty(execution => execution.UpdateBy, ownerUserId)
                        .SetProperty(execution => execution.UpdateTime, startedAt),
                cancellationToken
            )
            .ConfigureAwait(false) == 1;

    public async Task<bool> MarkCancellationRequestedAsync(
        Guid turnId,
        string ownerUserId,
        string reason,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken
    )
    {
        var execution = await _dbContext
            .BackgroundExecutions.SingleOrDefaultAsync(
                item =>
                    item.Id == turnId
                    && item.CreateBy == ownerUserId
                    && (
                        item.State == Agw.Agents.Contracts.Execution.AgentExecutionState.Queued
                        || item.State == Agw.Agents.Contracts.Execution.AgentExecutionState.Running
                        || item.State == Agw.Agents.Contracts.Execution.AgentExecutionState.WaitingForHuman
                    ),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (execution == null)
        {
            return false;
        }

        execution.Error = reason;
        execution.UpdateBy = ownerUserId;
        execution.UpdateTime = requestedAt;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<BackgroundExecution>> ListNonTerminalAsync(CancellationToken cancellationToken) =>
        await _dbContext
            .BackgroundExecutions.IgnoreUserScope()
            .AsNoTracking()
            .Where(execution =>
                execution.State == Agw.Agents.Contracts.Execution.AgentExecutionState.Queued
                || execution.State == Agw.Agents.Contracts.Execution.AgentExecutionState.Running
                || execution.State == Agw.Agents.Contracts.Execution.AgentExecutionState.WaitingForHuman
            )
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
}
