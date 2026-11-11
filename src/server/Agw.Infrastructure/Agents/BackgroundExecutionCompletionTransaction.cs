using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Execution;
using Agw.Auth.Contracts;
using Agw.Events.Contracts;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Agents;

public sealed class BackgroundExecutionCompletionTransaction : IBackgroundExecutionCompletionTransaction
{
    private readonly AgwDbContext _dbContext;
    private readonly IEventStaging _eventStaging;
    private readonly IApplicationLock _applicationLock;

    public BackgroundExecutionCompletionTransaction(
        AgwDbContext dbContext,
        IEventStaging eventStaging,
        IApplicationLock applicationLock
    )
    {
        _dbContext = dbContext;
        _eventStaging = eventStaging;
        _applicationLock = applicationLock;
    }

    public async Task<bool> RegisterCancelledAsync(BackgroundExecution tombstone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tombstone);
        var ownerUserId = string.IsNullOrWhiteSpace(tombstone.CreateBy)
            ? throw new AgwException(ErrorCodes.AuthenticationRequired)
            : tombstone.CreateBy.Trim();
        await using var lease = await _applicationLock.AcquireAsync(GetLockName(tombstone.Id), cancellationToken);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        using var userScope = PushUser(ownerUserId);
        if (
            await _dbContext
                .BackgroundExecutions.IgnoreUserScope()
                .AsNoTracking()
                .AnyAsync(execution => execution.Id == tombstone.Id, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            return false;
        }

        if (tombstone.State != AgentExecutionState.Interrupted || !tombstone.CompletedAt.HasValue)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "A cancellation tombstone must be Interrupted and complete."
            );
        }

        _dbContext.BackgroundExecutions.Add(tombstone);
        await StageCompletionEventAsync(tombstone, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> CompleteAsync(BackgroundExecutionCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (
            completion.State
            is not (AgentExecutionState.Completed or AgentExecutionState.Failed or AgentExecutionState.Interrupted)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "A background execution completion must be terminal.");
        }

        await using var lease = await _applicationLock.AcquireAsync(GetLockName(completion.TurnId), cancellationToken);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        using var userScope = PushUser(completion.OwnerUserId);
        var execution = await _dbContext
            .BackgroundExecutions.SingleOrDefaultAsync(
                item => item.Id == completion.TurnId && item.CreateBy == completion.OwnerUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (execution == null || IsTerminal(execution.State))
        {
            return false;
        }

        execution.State = completion.State;
        execution.ResultText = completion.ResultText;
        execution.Error = completion.Error ?? execution.Error;
        execution.InputTokens = completion.InputTokens;
        execution.OutputTokens = completion.OutputTokens;
        execution.CompletedAt = completion.CompletedAt;
        await StageCompletionEventAsync(execution, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Task<EventPublishResult> StageCompletionEventAsync(
        BackgroundExecution execution,
        CancellationToken cancellationToken
    )
    {
        var data = new
        {
            turnId = execution.Id,
            conversationId = execution.ConversationId,
            caller = execution.CallerKey,
            correlationKey = execution.CorrelationKey,
            state = execution.State.ToString(),
            resultText = execution.ResultText,
            error = execution.Error,
            inputTokens = execution.InputTokens,
            outputTokens = execution.OutputTokens,
        };
        var json = JsonSerializer.SerializeToElement(data);
        return _eventStaging.StageAsync(
            new EventStagingRequest
            {
                PublisherKey = "agw.execution",
                Event = new CloudEventDraft
                {
                    Id = $"{execution.Id:D}:completed",
                    Type = "agw.execution.completed",
                    Source = "/agw/executions",
                    Time = execution.CompletedAt,
                    Subject = execution.Id.ToString("D"),
                    DataContentType = "application/json",
                    Data = new CloudEventData
                    {
                        Kind = CloudEventDataKind.Json,
                        Text = json.GetRawText(),
                        Json = json,
                    },
                    ParentEventId = execution.ParentEventId,
                },
                InheritParentHops = true,
                ChainId = execution.ChainId,
                Hops = execution.Hops,
            },
            cancellationToken
        );
    }

    private static bool IsTerminal(AgentExecutionState state) =>
        state is AgentExecutionState.Completed or AgentExecutionState.Failed or AgentExecutionState.Interrupted;

    private static string GetLockName(Guid turnId) => $"agw:background-execution:{turnId:N}";

    private static IDisposable PushUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "BackgroundExecution")
            )
        );
}
