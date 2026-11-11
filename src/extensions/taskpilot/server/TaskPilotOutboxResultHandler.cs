using System.Text.Json;
using Agw.Agents.Contracts.Execution;
using Agw.Events.Contracts;
using Agw.Extensions.Sdk;
using Microsoft.EntityFrameworkCore;

namespace Agw.Extensions.TaskPilot;

internal sealed class TaskPilotOutboxResultHandler : IExtensionOutboxResultHandler
{
    private readonly TaskPilotDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public TaskPilotOutboxResultHandler(TaskPilotDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public static IReadOnlyList<string> Kinds { get; } =
    [
        ExtensionOutboxKind.UpsertManagedSubscription,
        ExtensionOutboxKind.DeleteManagedSubscription,
        ExtensionOutboxKind.UpsertSchedule,
        ExtensionOutboxKind.CancelSchedule,
        ExtensionOutboxKind.SubmitExecution,
        ExtensionOutboxKind.CancelExecution,
        ExtensionOutboxKind.Notify,
        ExtensionOutboxKind.UpdateNotification,
    ];

    public string GetResultLeaseName(IExtensionContext context) =>
        TaskPilotCaller.MutationLeaseName(TaskPilotCaller.Require(context).UserId!);

    public async ValueTask HandleAsync(ExtensionOutboxResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Kind)
        {
            case ExtensionOutboxKind.UpsertManagedSubscription:
            case ExtensionOutboxKind.DeleteManagedSubscription:
                await HandleSubscriptionAsync(result, cancellationToken);
                break;
            case ExtensionOutboxKind.UpsertSchedule:
            case ExtensionOutboxKind.CancelSchedule:
                await HandleScheduleAsync(result, cancellationToken);
                break;
            case ExtensionOutboxKind.SubmitExecution:
                await HandleSubmitExecutionAsync(result, cancellationToken);
                break;
            case ExtensionOutboxKind.CancelExecution:
                await HandleCancelExecutionAsync(result, cancellationToken);
                break;
            case ExtensionOutboxKind.Notify:
                await HandleNotificationCreateAsync(result, cancellationToken);
                break;
            case ExtensionOutboxKind.UpdateNotification:
                await HandleNotificationUpdateAsync(result, cancellationToken);
                break;
        }
    }

    private async Task HandleSubscriptionAsync(ExtensionOutboxResult result, CancellationToken cancellationToken)
    {
        TaskPilotUserState? userState = await _dbContext.UserStates.SingleOrDefaultAsync(cancellationToken);
        if (userState?.SystemSubscriptionOutboxId == result.Id)
        {
            userState.SystemSubscriptionOutboxId = null;
            if (
                result.State == ExtensionOutboxState.Completed
                && TryRead<ManagedSubscriptionState>(result) is { } state
            )
            {
                userState.SystemSubscriptionId =
                    state.Status == ManagedSubscriptionStatus.Active ? state.SubscriptionId : null;
                userState.SystemSubscriptionError = state.Accepted
                    ? null
                    : "The TaskPilot system subscription request was superseded.";
            }
            else
            {
                userState.SystemSubscriptionError = ResultError(result);
            }

            return;
        }

        TaskPilotRule? rule = await _dbContext.Rules.SingleOrDefaultAsync(
            row => row.SubscriptionOutboxId == result.Id,
            cancellationToken
        );
        if (rule is null)
        {
            return;
        }

        rule.SubscriptionOutboxId = null;
        if (result.State == ExtensionOutboxState.Completed && TryRead<ManagedSubscriptionState>(result) is { } managed)
        {
            rule.SubscriptionId = managed.Status == ManagedSubscriptionStatus.Active ? managed.SubscriptionId : null;
            rule.SubscriptionError = managed.Accepted ? null : "The managed subscription request was superseded.";
        }
        else
        {
            rule.SubscriptionError = ResultError(result);
        }
    }

    private async Task HandleScheduleAsync(ExtensionOutboxResult result, CancellationToken cancellationToken)
    {
        TaskPilotReview? review = await _dbContext.Reviews.SingleOrDefaultAsync(
            row => row.ScheduleOutboxId == result.Id,
            cancellationToken
        );
        if (review is null)
        {
            return;
        }

        review.ScheduleOutboxId = null;
        if (result.State != ExtensionOutboxState.Completed || TryRead<ScheduleState>(result) is not { } state)
        {
            return;
        }

        if (!state.Accepted || state.RequestSeq != review.RequestSeq)
        {
            return;
        }

        review.ScheduleId = state.ScheduleId;
        if (result.Kind == ExtensionOutboxKind.UpsertSchedule)
        {
            review.DueAt = state.DueAt;
        }
        else
        {
            review.State = TaskPilotReviewState.Cancelled;
            review.DueAt = null;
        }
        review.Version++;
    }

    private async Task HandleSubmitExecutionAsync(ExtensionOutboxResult result, CancellationToken cancellationToken)
    {
        TaskPilotRoundAttempt? attempt = await _dbContext.RoundAttempts.SingleOrDefaultAsync(
            row => row.SubmitOutboxId == result.Id,
            cancellationToken
        );
        if (attempt is null)
        {
            return;
        }

        TaskPilotRound? round = await _dbContext.Rounds.SingleOrDefaultAsync(
            row => row.Id == attempt.RootId,
            cancellationToken
        );
        if (round is null || round.TurnId != attempt.TurnId || round.Attempt != attempt.Attempt)
        {
            return;
        }

        if (
            result.State == ExtensionOutboxState.Completed
            && TryRead<BackgroundExecutionReceipt>(result) is { } receipt
        )
        {
            if (round.State is not (TaskPilotRoundState.Submitting or TaskPilotRoundState.ReceiptRepair))
            {
                return;
            }

            attempt.State = TaskPilotAttemptState.Running;
            attempt.StartedAt ??= _timeProvider.GetUtcNow();
            if (receipt.ConversationId != Guid.Empty)
            {
                round.ConversationId = receipt.ConversationId;
                TaskPilotMatter? matter = await _dbContext.Matters.SingleOrDefaultAsync(
                    row => row.Id == round.RootId,
                    cancellationToken
                );
                if (matter is not null)
                {
                    TaskPilotRule? rule = await _dbContext.Rules.SingleOrDefaultAsync(
                        row => row.Id == matter.RootId,
                        cancellationToken
                    );
                    if (rule is { SessionPolicy: TaskPilotSessionPolicy.PerMatter })
                    {
                        matter.ConversationId = receipt.ConversationId;
                        matter.Version++;
                    }
                }
            }

            if (round.State == TaskPilotRoundState.Submitting)
            {
                round.State = TaskPilotRoundState.Running;
                round.Version++;
                TaskPilotTelemetry.RecordRoundTransition(round.State);
            }
            return;
        }

        if (result.State != ExtensionOutboxState.Rejected)
        {
            return;
        }

        if (round.State is not (TaskPilotRoundState.Submitting or TaskPilotRoundState.ReceiptRepair))
        {
            return;
        }

        TaskPilotMatter? failedMatter = await _dbContext.Matters.SingleOrDefaultAsync(
            row => row.Id == round.RootId,
            cancellationToken
        );
        if (failedMatter is null)
        {
            return;
        }
        TaskPilotRule? failedRule = await _dbContext.Rules.SingleOrDefaultAsync(
            row => row.Id == failedMatter.RootId,
            cancellationToken
        );
        if (failedRule is null)
        {
            return;
        }

        attempt.State = TaskPilotAttemptState.Failed;
        attempt.Error = ResultError(result);
        attempt.EndedAt = _timeProvider.GetUtcNow();
        if (attempt.Purpose == TaskPilotAttemptPurpose.DeliveryReview)
        {
            await TaskPilotRoundTransitions.FailDeliveryReviewAsync(
                _dbContext,
                _timeProvider,
                round,
                attempt,
                attempt.Error,
                cancellationToken
            );
            return;
        }

        await TaskPilotRoundTransitions.FailAsync(
            _dbContext,
            TaskPilotJsonContext.Default.Options,
            _timeProvider,
            failedRule,
            failedMatter,
            round,
            attempt,
            attempt.Error,
            stageWake: true,
            cancellationToken
        );
    }

    private async Task HandleCancelExecutionAsync(ExtensionOutboxResult result, CancellationToken cancellationToken)
    {
        if (result.State != ExtensionOutboxState.Rejected)
        {
            return;
        }

        TaskPilotRoundAttempt? attempt = await _dbContext.RoundAttempts.SingleOrDefaultAsync(
            row => row.CancelOutboxId == result.Id,
            cancellationToken
        );
        if (attempt is null)
        {
            return;
        }

        TaskPilotRound? round = await _dbContext.Rounds.SingleOrDefaultAsync(
            row => row.Id == attempt.RootId,
            cancellationToken
        );
        if (round is not { State: TaskPilotRoundState.Cancelling } || round.TurnId != attempt.TurnId)
        {
            return;
        }

        TaskPilotMatter? matter = await _dbContext.Matters.SingleOrDefaultAsync(
            row => row.Id == round.RootId,
            cancellationToken
        );
        if (matter is null)
        {
            return;
        }
        TaskPilotRule? rule = await _dbContext.Rules.SingleOrDefaultAsync(
            row => row.Id == matter.RootId,
            cancellationToken
        );
        if (rule is null)
        {
            return;
        }

        attempt.State = TaskPilotAttemptState.Failed;
        attempt.Error = ResultError(result);
        attempt.EndedAt = _timeProvider.GetUtcNow();
        if (attempt.Purpose == TaskPilotAttemptPurpose.DeliveryReview)
        {
            await TaskPilotRoundTransitions.FailDeliveryReviewAsync(
                _dbContext,
                _timeProvider,
                round,
                attempt,
                attempt.Error,
                cancellationToken
            );
            return;
        }

        await TaskPilotRoundTransitions.FailAsync(
            _dbContext,
            TaskPilotJsonContext.Default.Options,
            _timeProvider,
            rule,
            matter,
            round,
            attempt,
            attempt.Error,
            stageWake: true,
            cancellationToken
        );
    }

    private async Task HandleNotificationCreateAsync(ExtensionOutboxResult result, CancellationToken cancellationToken)
    {
        TaskPilotCard? card = await _dbContext.Cards.SingleOrDefaultAsync(
            row => row.NotificationOutboxId == result.Id,
            cancellationToken
        );
        if (card is null)
        {
            return;
        }

        card.NotificationOutboxId = null;
        if (result.State != ExtensionOutboxState.Completed || result.ResultJson is null)
        {
            card.NotificationError = ResultError(result);
            return;
        }

        Guid notificationId = JsonSerializer.Deserialize<Guid>(result.ResultJson, TaskPilotJsonContext.Default.Options);
        if (notificationId == Guid.Empty)
        {
            card.NotificationError = "The notification facade returned an empty id.";
            return;
        }

        card.NotificationId = notificationId;
        card.NotificationVersion = Math.Max(1, card.NotificationVersion);
        card.NotificationError = null;
        if (card.State is not TaskPilotCardState.Open)
        {
            TaskPilotNotificationService.StageUpdateAsync(_dbContext, TaskPilotJsonContext.Default.Options, card);
        }
    }

    private async Task HandleNotificationUpdateAsync(ExtensionOutboxResult result, CancellationToken cancellationToken)
    {
        TaskPilotCard? card = await _dbContext.Cards.SingleOrDefaultAsync(
            row => row.NotificationUpdateOutboxId == result.Id,
            cancellationToken
        );
        if (card is null)
        {
            return;
        }

        long expectedVersion = card.NotificationUpdateExpectedVersion ?? Math.Max(1, card.NotificationVersion);
        card.NotificationUpdateOutboxId = null;
        card.NotificationUpdateExpectedVersion = null;
        if (result.State == ExtensionOutboxState.Completed)
        {
            card.NotificationVersion = Math.Max(card.NotificationVersion, expectedVersion + 1);
            card.NotificationError = null;
        }
        else
        {
            card.NotificationError = ResultError(result);
        }
        card.Version++;
    }

    private static T? TryRead<T>(ExtensionOutboxResult result)
        where T : class =>
        result.ResultJson is null
            ? null
            : JsonSerializer.Deserialize<T>(result.ResultJson, TaskPilotJsonContext.Default.Options);

    private static string ResultError(ExtensionOutboxResult result) =>
        string.IsNullOrWhiteSpace(result.Error)
            ? result.ErrorCode is { } code
                ? $"Extension outbox operation failed with code {code}."
                : "Extension outbox operation failed."
            : result.Error;
}
