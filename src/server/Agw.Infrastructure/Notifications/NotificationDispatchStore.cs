using Agw.Infrastructure.Data;
using Agw.Notifications.Application.Persistence;
using Agw.Notifications.Configuration;
using Agw.Shared.Data.Entities.Notifications;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Notifications;

public sealed class NotificationDispatchStore : INotificationDispatchStore
{
    private const int RecoveryBatchSize = 100;
    private readonly INotificationsDbContext _dbContext;
    private readonly INotificationTransaction _transaction;
    private readonly TimeProvider _timeProvider;
    private readonly NotificationDispatchOptions _options;

    public NotificationDispatchStore(
        INotificationsDbContext dbContext,
        INotificationTransaction transaction,
        TimeProvider timeProvider,
        IOptions<NotificationDispatchOptions> options
    )
    {
        _dbContext = dbContext;
        _transaction = transaction;
        _timeProvider = timeProvider;
        _options = options.Value;
    }

    public async Task<NotificationDispatchClaim?> TryClaimAsync(
        Guid dispatchId,
        string hostId,
        CancellationToken cancellationToken
    )
    {
        var now = _timeProvider.GetUtcNow();
        var claimExpiresAt = now.Add(_options.ClaimDuration);
        var attemptId = Guid.CreateVersion7();
        var updated = await _dbContext
            .NotificationDispatches.IgnoreUserScope()
            .Where(dispatch =>
                dispatch.Id == dispatchId
                && dispatch.ChannelKey != "desktop"
                && (
                    dispatch.State == NotificationDispatchState.Pending
                    || dispatch.State == NotificationDispatchState.UpdatePending
                    || (
                        dispatch.State == NotificationDispatchState.Failed
                        && dispatch.NextAttemptAt.HasValue
                        && dispatch.NextAttemptAt <= now
                    )
                    || (
                        dispatch.State == NotificationDispatchState.Sending
                        && dispatch.ClaimExpiresAt.HasValue
                        && dispatch.ClaimExpiresAt <= now
                    )
                )
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(dispatch => dispatch.State, NotificationDispatchState.Sending)
                        .SetProperty(dispatch => dispatch.Attempt, dispatch => dispatch.Attempt + 1)
                        .SetProperty(dispatch => dispatch.NextAttemptAt, (DateTimeOffset?)null)
                        .SetProperty(dispatch => dispatch.ClaimedBy, hostId)
                        .SetProperty(dispatch => dispatch.ClaimAttemptId, (Guid?)attemptId)
                        .SetProperty(dispatch => dispatch.ClaimExpiresAt, claimExpiresAt)
                        .SetProperty(dispatch => dispatch.RecoverAfter, now.Add(_options.RecoveryStaleAfter)),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (updated == 0)
            return null;

        var dispatch = await _dbContext
            .NotificationDispatches.IgnoreUserScope()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                dispatch => dispatch.Id == dispatchId && dispatch.ClaimAttemptId == attemptId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (dispatch is null)
            return null;
        var notification = await _dbContext
            .Notifications.IgnoreUserScope()
            .AsNoTracking()
            .SingleOrDefaultAsync(notification => notification.Id == dispatch.NotificationId, cancellationToken)
            .ConfigureAwait(false);
        if (notification is null)
            throw new AgwException(ErrorCodes.NotificationNotFound);
        var question =
            notification.Kind == NotificationKind.Question
                ? await _dbContext
                    .NotificationQuestions.IgnoreUserScope()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(question => question.NotificationId == notification.Id, cancellationToken)
                    .ConfigureAwait(false)
                : null;
        if (notification.Kind == NotificationKind.Question && question is null)
            throw new AgwException(ErrorCodes.NotificationNotFound);

        var attempt = new NotificationDispatchAttempt
        {
            Id = attemptId,
            DispatchId = dispatch.Id,
            Attempt = dispatch.Attempt,
            MessageVersion = dispatch.MessageVersion,
            StartedAt = now,
            Result = NotificationDispatchAttemptResult.Sending,
        };
        _dbContext.NotificationDispatchAttempts.Add(attempt);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new NotificationDispatchClaim
        {
            Dispatch = dispatch,
            Notification = notification,
            Question = question,
            AttemptId = attempt.Id,
        };
    }

    public async Task<bool> RenewAsync(
        Guid dispatchId,
        Guid attemptId,
        string hostId,
        CancellationToken cancellationToken
    )
    {
        var now = _timeProvider.GetUtcNow();
        var renewed = await _dbContext
            .NotificationDispatches.IgnoreUserScope()
            .Where(dispatch =>
                dispatch.Id == dispatchId
                && dispatch.State == NotificationDispatchState.Sending
                && dispatch.ClaimedBy == hostId
                && dispatch.ClaimAttemptId == attemptId
                && dispatch.ClaimExpiresAt.HasValue
                && dispatch.ClaimExpiresAt > now
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(dispatch => dispatch.ClaimExpiresAt, now.Add(_options.ClaimDuration))
                        .SetProperty(dispatch => dispatch.RecoverAfter, now.Add(_options.RecoveryStaleAfter)),
                cancellationToken
            )
            .ConfigureAwait(false);
        return renewed == 1;
    }

    public async Task<bool> CompleteAsync(
        Guid dispatchId,
        Guid attemptId,
        string hostId,
        long sentVersion,
        NotificationDispatchAttemptResult result,
        string? externalMessageId,
        string? error,
        CancellationToken cancellationToken
    )
    {
        var now = _timeProvider.GetUtcNow();
        var dispatch = await _dbContext
            .NotificationDispatches.IgnoreUserScope()
            .SingleOrDefaultAsync(
                dispatch =>
                    dispatch.Id == dispatchId
                    && dispatch.State == NotificationDispatchState.Sending
                    && dispatch.ClaimedBy == hostId
                    && dispatch.ClaimAttemptId == attemptId
                    && dispatch.ClaimExpiresAt.HasValue
                    && dispatch.ClaimExpiresAt > now,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (dispatch is null)
            return false;
        var attempt = await _dbContext
            .NotificationDispatchAttempts.IgnoreUserScope()
            .SingleOrDefaultAsync(
                attempt => attempt.Id == attemptId && attempt.DispatchId == dispatch.Id,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (
            attempt is null
            || attempt.Attempt != dispatch.Attempt
            || attempt.Result != NotificationDispatchAttemptResult.Sending
            || attempt.FinishedAt.HasValue
        )
            return false;

        attempt.FinishedAt = now;
        attempt.Result = result;
        attempt.ExternalMessageId = externalMessageId;
        attempt.Error = LimitError(error);
        if (result == NotificationDispatchAttemptResult.Sent)
        {
            if (externalMessageId is not null)
                dispatch.ExternalMessageId = externalMessageId;
            dispatch.SentVersion = sentVersion;
            dispatch.LastError = null;
            dispatch.NextAttemptAt = null;
            if (dispatch.MessageVersion > sentVersion)
            {
                dispatch.State = NotificationDispatchState.UpdatePending;
                dispatch.RecoverAfter = now.Add(_options.RecoveryStaleAfter);
                _transaction.StageDispatch(dispatch);
            }
            else
            {
                dispatch.State = NotificationDispatchState.Sent;
                dispatch.RecoverAfter = now.Add(_options.RecoveryStaleAfter);
            }
        }
        else if (result == NotificationDispatchAttemptResult.Unsupported)
        {
            dispatch.State = NotificationDispatchState.Unsupported;
            dispatch.LastError = null;
            dispatch.NextAttemptAt = null;
            dispatch.RecoverAfter = now.Add(_options.RecoveryStaleAfter);
        }
        else
        {
            dispatch.LastError = LimitError(error ?? "Notification channel send failed.");
            if (dispatch.Attempt >= _options.MaxAttempts)
            {
                dispatch.State = NotificationDispatchState.Parked;
                dispatch.NextAttemptAt = null;
                dispatch.RecoverAfter = now.Add(_options.RecoveryStaleAfter);
            }
            else
            {
                var nextAttemptAt = now.Add(GetBackoff(dispatch.Attempt));
                dispatch.State = NotificationDispatchState.Failed;
                dispatch.NextAttemptAt = nextAttemptAt;
                dispatch.RecoverAfter = nextAttemptAt;
            }
        }
        dispatch.ClaimedBy = null;
        dispatch.ClaimAttemptId = null;
        dispatch.ClaimExpiresAt = null;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        return true;
    }

    public async Task<int> RecoverDueAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var dispatches = await _dbContext
            .NotificationDispatches.IgnoreUserScope()
            .Where(dispatch =>
                dispatch.ChannelKey != "desktop"
                && dispatch.State == NotificationDispatchState.Failed
                && dispatch.NextAttemptAt.HasValue
                && dispatch.NextAttemptAt <= now
            )
            .OrderBy(dispatch => dispatch.RecoverAfter)
            .Take(RecoveryBatchSize)
            .Select(dispatch => new RecoveryIdentity
            {
                Id = dispatch.Id,
                MessageVersion = dispatch.MessageVersion,
                NextAttemptAt = dispatch.NextAttemptAt,
                ClaimAttemptId = dispatch.ClaimAttemptId,
                ClaimExpiresAt = dispatch.ClaimExpiresAt,
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (dispatches.Length == 0)
            return 0;

        var recovered = 0;
        await _transaction
            .ExecuteAsync(
                async token =>
                {
                    foreach (var identity in dispatches)
                    {
                        if (await TryRecoverDispatchAsync(identity, now, token).ConfigureAwait(false))
                            recovered++;
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        return recovered;
    }

    private async Task<bool> TryRecoverDispatchAsync(
        RecoveryIdentity identity,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var updated = await _dbContext
            .NotificationDispatches.IgnoreUserScope()
            .Where(dispatch =>
                dispatch.Id == identity.Id
                && dispatch.ChannelKey != "desktop"
                && dispatch.State == NotificationDispatchState.Failed
                && dispatch.NextAttemptAt == identity.NextAttemptAt
                && dispatch.NextAttemptAt.HasValue
                && dispatch.NextAttemptAt <= now
                && dispatch.MessageVersion == identity.MessageVersion
                && dispatch.ClaimAttemptId == identity.ClaimAttemptId
                && dispatch.ClaimExpiresAt == identity.ClaimExpiresAt
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(dispatch => dispatch.State, NotificationDispatchState.Pending)
                        .SetProperty(dispatch => dispatch.NextAttemptAt, (DateTimeOffset?)null)
                        .SetProperty(dispatch => dispatch.RecoverAfter, now.Add(_options.RecoveryStaleAfter)),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (updated == 0)
            return false;

        var dispatch = await _dbContext
            .NotificationDispatches.IgnoreUserScope()
            .SingleAsync(item => item.Id == identity.Id, cancellationToken)
            .ConfigureAwait(false);
        _transaction.StageDispatch(dispatch);
        return true;
    }

    private sealed record RecoveryIdentity
    {
        public required Guid Id { get; init; }
        public required long MessageVersion { get; init; }
        public required DateTimeOffset? NextAttemptAt { get; init; }
        public required Guid? ClaimAttemptId { get; init; }
        public required DateTimeOffset? ClaimExpiresAt { get; init; }
    }

    private TimeSpan GetBackoff(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 20);
        var seconds = Math.Min(
            _options.BaseBackoff.TotalSeconds * Math.Pow(2, exponent),
            _options.MaxBackoff.TotalSeconds
        );
        return TimeSpan.FromSeconds(seconds);
    }

    private static string? LimitError(string? error) => error is { Length: > 2000 } ? error[..2000] : error;
}
