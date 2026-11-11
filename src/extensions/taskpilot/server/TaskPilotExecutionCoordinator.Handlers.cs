using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Agw.Agents.Contracts.Execution;
using Agw.Agents.Contracts.Messages;
using Agw.Events.Contracts;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Notifications.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Extensions.TaskPilot;

internal sealed partial class TaskPilotExecutionCoordinator
{
    private const int MaxReceiptRepairs = 2;

    public async Task<ExtensionEventHandlerResult> HandleExecutionCompletedAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        ExtensionEventDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        if (
            delivery.Event.Data is not { Kind: CloudEventDataKind.Json, Json: { ValueKind: JsonValueKind.Object } data }
            || !TryString(data, "caller", out string caller)
            || !string.Equals(caller, $"ext:{TaskPilotExtension.ExtensionId:D}", StringComparison.Ordinal)
            || !TryString(data, "correlationKey", out string correlationKey)
            || !TryReadRoundCorrelation(correlationKey, out Guid roundId, out int attemptNumber)
            || !TryGuid(data, "turnId", out Guid turnId)
            || !TryString(data, "state", out string stateText)
            || !Enum.TryParse(stateText, out AgentExecutionState executionState)
            || executionState
                is not (AgentExecutionState.Completed or AgentExecutionState.Failed or AgentExecutionState.Interrupted)
        )
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        TaskPilotRound? round = await dbContext.Rounds.SingleOrDefaultAsync(
            row => row.Id == roundId,
            cancellationToken
        );
        if (
            round is null
            || round.TurnId != turnId
            || round.Attempt != attemptNumber
            || round.State
                is not (
                    TaskPilotRoundState.Submitting
                    or TaskPilotRoundState.Running
                    or TaskPilotRoundState.ReceiptRepair
                    or TaskPilotRoundState.Cancelling
                )
        )
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        TaskPilotRoundAttempt? attempt = await dbContext.RoundAttempts.SingleOrDefaultAsync(
            row => row.RootId == round.Id && row.Attempt == attemptNumber && row.TurnId == turnId,
            cancellationToken
        );
        if (attempt is null || attempt.Purpose == TaskPilotAttemptPurpose.DeliveryReview)
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        TaskPilotMatter matter =
            await dbContext.Matters.SingleOrDefaultAsync(row => row.Id == round.RootId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        TaskPilotRule rule =
            await dbContext.Rules.SingleOrDefaultAsync(row => row.Id == matter.RootId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        string? resultText = TryString(data, "resultText", out string? text) ? text : null;
        string? error = TryString(data, "error", out string? executionError) ? executionError : null;
        RecordExecutionResult(rule, matter, round, attempt, data, now);

        if (round.State == TaskPilotRoundState.Cancelling)
        {
            await FinishCancelledRoundAsync(
                dbContext,
                context,
                matter,
                round,
                attempt,
                resultText,
                error,
                now,
                cancellationToken
            );
        }
        else if (executionState != AgentExecutionState.Completed)
        {
            attempt.State = TaskPilotAttemptState.Failed;
            attempt.Error = error ?? $"Background execution ended in {executionState}.";
            await FailRoundAndContinueAsync(
                dbContext,
                context,
                rule,
                matter,
                round,
                attempt,
                attempt.Error,
                cancellationToken
            );
        }
        else
        {
            await HandleCompletedReceiptAsync(
                dbContext,
                context,
                rule,
                matter,
                round,
                attempt,
                resultText,
                cancellationToken
            );
        }

        return ExtensionEventHandlerResult.Succeeded;
    }

    private static void RecordExecutionResult(
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotRoundAttempt attempt,
        JsonElement data,
        DateTimeOffset now
    )
    {
        round.ConversationId =
            TryGuid(data, "conversationId", out Guid conversationId) && conversationId != Guid.Empty
                ? conversationId
                : round.ConversationId;
        if (rule.SessionPolicy == TaskPilotSessionPolicy.PerMatter && round.ConversationId is { } matterConversationId)
        {
            matter.ConversationId = matterConversationId;
        }

        long inputTokens = TryInt64(data, "inputTokens", out long input) ? input : 0;
        long outputTokens = TryInt64(data, "outputTokens", out long output) ? output : 0;
        round.InputTokens += inputTokens;
        round.OutputTokens += outputTokens;
        attempt.InputTokens = inputTokens;
        attempt.OutputTokens = outputTokens;
        attempt.EndedAt = now;
    }

    private async Task FinishCancelledRoundAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotRoundAttempt attempt,
        string? resultText,
        string? error,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var parsed = TaskPilotReceiptParser.Parse(resultText ?? string.Empty, context.JsonOptions);
        round.Summary = parsed.Summary;
        if (parsed.IsValid)
            round.Outcome = parsed.Outcome;
        else
            round.ReceiptError = parsed.Error;
        round.State = TaskPilotRoundState.Cancelled;
        round.EndedAt = now;
        round.Version++;
        attempt.State = TaskPilotAttemptState.Cancelled;
        attempt.Error = error;
        if (matter.State == TaskPilotMatterState.WaitingHuman)
        {
            matter.State = TaskPilotMatterState.Active;
        }
        matter.Version++;
        await TaskPilotRoundTransitions.ReleaseOccupancyAsync(dbContext, round.Id, cancellationToken);
        TaskPilotTelemetry.RecordRoundTransition(round.State);
        await StartRunnableAsync(dbContext, context, cancellationToken);
    }

    private async Task HandleCompletedReceiptAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotRoundAttempt attempt,
        string? resultText,
        CancellationToken cancellationToken
    )
    {
        TaskPilotReceiptParseResult parsed = ParseCompletedReceipt(rule, context, resultText);

        attempt.State = parsed.IsValid ? TaskPilotAttemptState.Completed : TaskPilotAttemptState.Failed;
        attempt.Error = parsed.Error;
        if (!parsed.IsValid)
        {
            await HandleInvalidReceiptAsync(
                dbContext,
                context,
                rule,
                matter,
                round,
                attempt,
                parsed,
                cancellationToken
            );
            return;
        }

        await HandleValidReceiptAsync(dbContext, context, rule, matter, round, attempt, parsed, cancellationToken);
    }

    private static TaskPilotReceiptParseResult ParseCompletedReceipt(
        TaskPilotRule rule,
        IExtensionContext context,
        string? resultText
    )
    {
        TaskPilotReceiptParseResult parsed = TaskPilotReceiptParser.Parse(
            resultText ?? string.Empty,
            context.JsonOptions
        );
        if (parsed.IsValid && parsed.Receipt is { } receipt)
        {
            string? semanticError = ValidateReceiptSemantics(rule, receipt, context.JsonOptions);
            if (semanticError is not null)
            {
                parsed = parsed with { IsValid = false, Error = semanticError };
            }
        }

        return parsed;
    }

    private async Task HandleInvalidReceiptAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotRoundAttempt attempt,
        TaskPilotReceiptParseResult parsed,
        CancellationToken cancellationToken
    )
    {
        bool alreadyRepairing = UpdateReceiptRepairState(round, parsed);
        int repairCount = await dbContext.RoundAttempts.CountAsync(
            row => row.RootId == round.Id && row.Purpose == TaskPilotAttemptPurpose.ReceiptRepair,
            cancellationToken
        );
        if (repairCount >= MaxReceiptRepairs)
        {
            await FailRoundAndContinueAsync(
                dbContext,
                context,
                rule,
                matter,
                round,
                attempt,
                parsed.Error,
                cancellationToken
            );
            return;
        }

        if (alreadyRepairing)
        {
            round.ReceiptError = parsed.Error;
            round.Version++;
        }
        else
            TaskPilotTelemetry.RecordReceiptRepair();

        await SubmitReceiptRepairAsync(dbContext, context, rule, matter, round, parsed.Error!, cancellationToken);
    }

    private static bool UpdateReceiptRepairState(TaskPilotRound round, TaskPilotReceiptParseResult parsed)
    {
        bool alreadyRepairing = round.State == TaskPilotRoundState.ReceiptRepair;
        if (!alreadyRepairing)
        {
            round.Outcome = parsed.Outcome;
            round.Summary = parsed.Summary;
            round.ReceiptError = parsed.Error;
            round.State = TaskPilotRoundState.ReceiptRepair;
            round.Version++;
        }

        return alreadyRepairing;
    }

    private async Task HandleValidReceiptAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotRoundAttempt attempt,
        TaskPilotReceiptParseResult parsed,
        CancellationToken cancellationToken
    )
    {
        UpdateCompletedReceiptState(round, parsed);
        if (parsed.Outcome == TaskPilotRoundOutcome.RetryableFailure)
        {
            await FailRoundAndContinueAsync(
                dbContext,
                context,
                rule,
                matter,
                round,
                attempt,
                parsed.Summary,
                cancellationToken
            );
            return;
        }

        await CompleteReceiptRoundAsync(dbContext, context, rule, matter, round, parsed.Receipt!, cancellationToken);
    }

    private static void UpdateCompletedReceiptState(TaskPilotRound round, TaskPilotReceiptParseResult parsed)
    {
        round.Outcome = parsed.Outcome;
        if (
            round.State != TaskPilotRoundState.ReceiptRepair
            || parsed.Outcome == TaskPilotRoundOutcome.RetryableFailure
        )
        {
            round.Summary = parsed.Summary;
        }
        round.ReceiptJson = JsonSerializer.Serialize(parsed.Receipt, TaskPilotJsonContext.Default.TaskPilotReceipt);
        round.ReceiptError = null;
    }

    private async Task CompleteReceiptRoundAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotReceipt receipt,
        CancellationToken cancellationToken
    )
    {
        await ApplyReceiptAsync(dbContext, context, rule, matter, round, receipt, cancellationToken);
        await TaskPilotRoundTransitions.CompleteAsync(
            dbContext,
            context.JsonOptions,
            _timeProvider,
            matter,
            round,
            cancellationToken
        );
        await StartRunnableAsync(dbContext, context, cancellationToken);
    }

    private async Task FailRoundAndContinueAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotRoundAttempt attempt,
        string? error,
        CancellationToken cancellationToken
    )
    {
        await TaskPilotRoundTransitions.FailAsync(
            dbContext,
            context.JsonOptions,
            _timeProvider,
            rule,
            matter,
            round,
            attempt,
            error,
            stageWake: false,
            cancellationToken
        );
        await StartRunnableAsync(dbContext, context, cancellationToken);
    }

    public async Task<ExtensionEventHandlerResult> HandleNotificationAnsweredAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        ExtensionEventDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        if (
            delivery.Event.Data is not { Kind: CloudEventDataKind.Json, Json: { ValueKind: JsonValueKind.Object } data }
            || !TryString(data, "origin", out string origin)
            || !string.Equals(origin, TaskPilotExtension.ExtensionId.ToString("D"), StringComparison.Ordinal)
            || !TryString(data, "correlationKey", out string correlationKey)
            || !correlationKey.StartsWith("card:", StringComparison.Ordinal)
            || !Guid.TryParse(correlationKey.AsSpan("card:".Length), out Guid cardId)
            || !TryGuid(data, "notificationId", out Guid notificationId)
        )
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        TaskPilotCard? card = await dbContext.Cards.SingleOrDefaultAsync(row => row.Id == cardId, cancellationToken);
        if (card is not { State: TaskPilotCardState.Open } || card.NotificationId != notificationId)
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        TaskPilotMatter matter =
            await dbContext.Matters.SingleOrDefaultAsync(row => row.Id == card.RootId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        TaskPilotRound askingRound =
            await dbContext.Rounds.SingleOrDefaultAsync(
                row => row.Id == card.RoundId && row.RootId == matter.Id,
                cancellationToken
            ) ?? throw new AgwException(ErrorCodes.ResourceNotFound);

        card.State = TaskPilotCardState.Answered;
        card.OpenCardKey = null;
        card.AnswerJson = data.GetRawText();
        card.NotificationVersion = Math.Max(1, card.NotificationVersion) + 1;
        card.NotificationError = null;
        card.Version++;

        bool otherQuestionOpen = await dbContext.Cards.AnyAsync(
            row =>
                row.RootId == matter.Id
                && row.Id != card.Id
                && row.Kind == TaskPilotCardKind.Ask
                && row.State == TaskPilotCardState.Open,
            cancellationToken
        );
        if (matter.State is not (TaskPilotMatterState.Paused or TaskPilotMatterState.Stopped))
        {
            matter.State = otherQuestionOpen ? TaskPilotMatterState.WaitingHuman : TaskPilotMatterState.Active;
        }
        matter.Version++;

        TaskPilotRound round = new()
        {
            RootId = matter.Id,
            RuleVersion = askingRound.RuleVersion,
            State = TaskPilotRoundState.Queued,
            Trigger = TaskPilotRoundTrigger.Answer,
            HeadEventId = askingRound.HeadEventId,
            ChainId = askingRound.ChainId,
            HeadHops = askingRound.HeadHops,
            ParentEventId = askingRound.HeadEventId,
            ConversationId = card.ConversationId,
            Version = 0,
        };
        dbContext.Rounds.Add(round);
        dbContext.RoundInputs.Add(
            new TaskPilotRoundInput
            {
                RootId = round.Id,
                EventId = delivery.EventId,
                Kind = TaskPilotRoundInputKind.Answer,
                PayloadJson = data.GetRawText(),
                ReceivedAt = delivery.ReceivedAt,
                ChainId = askingRound.ChainId,
                ParentEventId = askingRound.HeadEventId,
                Hops = askingRound.HeadHops,
                Summary = "A TaskPilot question was answered.",
            }
        );

        await dbContext.SaveChangesAsync(cancellationToken);
        await StartRunnableAsync(dbContext, context, cancellationToken);
        return ExtensionEventHandlerResult.Succeeded;
    }

    public async Task<ExtensionEventHandlerResult> HandleReviewDueAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        ExtensionEventDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        if (!TryReadEventData<TaskPilotReviewDueData>(delivery, out TaskPilotReviewDueData? data))
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        TaskPilotReview? review = await dbContext.Reviews.SingleOrDefaultAsync(
            row => row.Id == data.ReviewId && row.RootId == data.MatterId,
            cancellationToken
        );
        if (review is not { State: TaskPilotReviewState.Scheduled } || review.RequestSeq != data.RequestSeq)
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        if (await HasInputAsync(dbContext, delivery.EventId, cancellationToken))
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        TaskPilotMatter matter =
            await dbContext.Matters.SingleOrDefaultAsync(row => row.Id == review.RootId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        TaskPilotRule rule =
            await dbContext.Rules.SingleOrDefaultAsync(row => row.Id == matter.RootId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        review.State = TaskPilotReviewState.Due;
        review.Version++;
        TaskPilotRound round = NewScheduledRound(matter, rule, delivery, TaskPilotRoundTrigger.Review);
        dbContext.Rounds.Add(round);
        dbContext.RoundInputs.Add(
            NewScheduledInput(round, delivery, TaskPilotRoundInputKind.Review, "A TaskPilot review is due.")
        );
        matter.Version++;
        await dbContext.SaveChangesAsync(cancellationToken);
        await StartRunnableAsync(dbContext, context, cancellationToken);
        return ExtensionEventHandlerResult.Succeeded;
    }

    public async Task<ExtensionEventHandlerResult> HandleRetryDueAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        ExtensionEventDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        if (!TryReadEventData<TaskPilotRetryDueData>(delivery, out TaskPilotRetryDueData? data))
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        TaskPilotMatter? matter = await dbContext.Matters.SingleOrDefaultAsync(
            row => row.Id == data.MatterId,
            cancellationToken
        );
        if (matter is null || matter.State == TaskPilotMatterState.Stopped)
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        if (data.WakeOnly)
        {
            if (matter.Version != data.RequestSeq)
            {
                return ExtensionEventHandlerResult.Succeeded;
            }
            await StartRunnableAsync(dbContext, context, cancellationToken);
            return ExtensionEventHandlerResult.Succeeded;
        }

        if (
            matter.RetryRequestSeq != data.RequestSeq
            || await HasInputAsync(dbContext, delivery.EventId, cancellationToken)
        )
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        TaskPilotRule rule =
            await dbContext.Rules.SingleOrDefaultAsync(row => row.Id == matter.RootId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        TaskPilotRound round = NewScheduledRound(matter, rule, delivery, TaskPilotRoundTrigger.Retry);
        dbContext.Rounds.Add(round);
        dbContext.RoundInputs.Add(
            NewScheduledInput(round, delivery, TaskPilotRoundInputKind.Retry, "A TaskPilot retry is due.")
        );
        if (matter.State != TaskPilotMatterState.Paused)
        {
            matter.State = TaskPilotMatterState.Active;
        }
        matter.Version++;
        await dbContext.SaveChangesAsync(cancellationToken);
        await StartRunnableAsync(dbContext, context, cancellationToken);
        return ExtensionEventHandlerResult.Succeeded;
    }

    public async Task CancelRuleRoundsAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotRule rule,
        CancellationToken cancellationToken
    )
    {
        var candidates = await (
            from round in dbContext.Rounds
            join matter in dbContext.Matters on round.RootId equals matter.Id
            where
                matter.RootId == rule.Id
                && (
                    round.State == TaskPilotRoundState.Queued
                    || round.State == TaskPilotRoundState.Submitting
                    || round.State == TaskPilotRoundState.Running
                    || round.State == TaskPilotRoundState.ReceiptRepair
                    || round.State == TaskPilotRoundState.Cancelling
                )
            orderby round.CreateTime
            select new { RoundId = round.Id, MatterId = matter.Id }
        ).ToArrayAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            TaskPilotRound round = await dbContext.Rounds.SingleAsync(
                row => row.Id == candidate.RoundId,
                cancellationToken
            );
            TaskPilotMatter matter = await dbContext.Matters.SingleAsync(
                row => row.Id == candidate.MatterId,
                cancellationToken
            );
            await CancelRoundAsync(dbContext, context, matter, round, cancellationToken);
        }
    }

    public async Task CancelRoundAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotMatter matter,
        TaskPilotRound round,
        CancellationToken cancellationToken
    )
    {
        if (round.State == TaskPilotRoundState.Queued)
        {
            round.State = TaskPilotRoundState.Cancelled;
            round.EndedAt = _timeProvider.GetUtcNow();
            round.Version++;
            matter.Version++;
            await TaskPilotRoundTransitions.ReleaseOccupancyAsync(dbContext, round.Id, cancellationToken);
            TaskPilotTelemetry.RecordRoundTransition(round.State);
            await StartRunnableAsync(dbContext, context, cancellationToken);
            return;
        }

        if (round.State == TaskPilotRoundState.Cancelling)
        {
            return;
        }
        if (
            round.State
            is not (TaskPilotRoundState.Submitting or TaskPilotRoundState.Running or TaskPilotRoundState.ReceiptRepair)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        TaskPilotRoundAttempt attempt =
            await dbContext.RoundAttempts.SingleOrDefaultAsync(
                row => row.RootId == round.Id && row.Attempt == round.Attempt && row.TurnId == round.TurnId,
                cancellationToken
            ) ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        if (attempt.SubmitOutboxId is { } submitOutboxId)
        {
            await ExtensionOutbox.CancelPendingAsync(dbContext, submitOutboxId, cancellationToken);
        }

        string correlationKey;
        if (attempt.Purpose == TaskPilotAttemptPurpose.DeliveryReview)
        {
            TaskPilotDelivery delivery =
                await dbContext.Deliveries.SingleOrDefaultAsync(
                    row => row.ReviewRoundId == round.Id && row.RootId == matter.Id,
                    cancellationToken
                ) ?? throw new AgwException(ErrorCodes.ResourceNotFound);
            correlationKey =
                $"delivery-review:{delivery.Id:D}:{delivery.HeadSha ?? throw new AgwException(ErrorCodes.InvalidParam)}";
        }
        else
        {
            correlationKey = $"round:{round.Id:D}:{round.Attempt}";
        }

        var request = new BackgroundExecutionCancelRequest(
            round.TurnId ?? throw new AgwException(ErrorCodes.InvalidParam),
            correlationKey,
            round.HeadEventId,
            "The TaskPilot round was cancelled."
        );
        attempt.CancelOutboxId = ExtensionOutbox.Enqueue(
            dbContext,
            ExtensionOutboxKind.CancelExecution,
            $"taskpilot:round:{round.Id:D}:attempt:{round.Attempt}:cancel",
            request,
            context.JsonOptions
        );
        round.State = TaskPilotRoundState.Cancelling;
        round.Version++;
        matter.Version++;
        TaskPilotTelemetry.RecordRoundTransition(round.State);
    }

    public async Task CancelReviewAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotReview review,
        CancellationToken cancellationToken
    )
    {
        if (review.State == TaskPilotReviewState.Cancelled)
        {
            return;
        }

        review.RequestSeq++;
        review.State = TaskPilotReviewState.Cancelled;
        review.DueAt = null;
        review.Version++;
        TaskPilotRoundTransitions.StageCancelSchedule(dbContext, context.JsonOptions, review);
        TaskPilotMatter? matter = await dbContext.Matters.SingleOrDefaultAsync(
            row => row.Id == review.RootId,
            cancellationToken
        );
        if (matter is not null)
        {
            matter.Version++;
        }
    }

    private async Task SubmitReceiptRepairAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        string receiptError,
        CancellationToken cancellationToken
    )
    {
        TaskPilotRuleRevision revision =
            await dbContext.RuleRevisions.SingleOrDefaultAsync(
                row => row.RootId == rule.Id && row.Version == round.RuleVersion,
                cancellationToken
            ) ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        TaskPilotRuleDetails snapshot =
            JsonSerializer.Deserialize(revision.SnapshotJson, TaskPilotJsonContext.Default.TaskPilotRuleDetails)
            ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        Guid turnId = Guid.CreateVersion7();
        AgwUserInput input = TaskPilotInputBuilder.BuildReceiptRepair(
            receiptError,
            round.Summary ?? string.Empty,
            turnId,
            _timeProvider
        );
        round.Attempt++;
        round.TurnId = turnId;
        round.State = TaskPilotRoundState.ReceiptRepair;
        round.StartedAt = now;
        round.Version++;
        TaskPilotRoundAttempt attempt = new()
        {
            RootId = round.Id,
            Attempt = round.Attempt,
            TurnId = turnId,
            Purpose = TaskPilotAttemptPurpose.ReceiptRepair,
            State = TaskPilotAttemptState.Submitting,
            StartedAt = now,
        };
        var request = new BackgroundExecutionRequest(
            turnId,
            new AgentTarget(snapshot.TargetKind, snapshot.TargetId),
            snapshot.ProjectId,
            round.ConversationId,
            matter.Title,
            input,
            snapshot.PermissionMode,
            BackgroundToolPolicy.None,
            $"round:{round.Id:D}:{round.Attempt}",
            round.HeadEventId
        );
        attempt.SubmitOutboxId = ExtensionOutbox.Enqueue(
            dbContext,
            ExtensionOutboxKind.SubmitExecution,
            $"taskpilot:round:{round.Id:D}:attempt:{round.Attempt}:submit",
            request,
            context.JsonOptions
        );
        dbContext.RoundAttempts.Add(attempt);
        TaskPilotTelemetry.RecordReceiptRepair();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyReceiptAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotReceipt receipt,
        CancellationToken cancellationToken
    )
    {
        JsonSerializerOptions jsonOptions = context.JsonOptions;
        int attentionCount = await StageAskCardsAsync(
            dbContext,
            context,
            jsonOptions,
            rule,
            matter,
            round,
            receipt,
            cancellationToken
        );
        attentionCount += StageNoticeCards(dbContext, context, jsonOptions, rule, matter, round, receipt);
        await ResolveReceiptCardsAsync(dbContext, jsonOptions, matter, receipt, cancellationToken);
        await StageReceiptReviewsAsync(dbContext, context, matter, round, receipt, cancellationToken);
        EnqueueReceiptEvents(dbContext, jsonOptions, rule, round, receipt);
        StageReceiptSummary(dbContext, context, jsonOptions, rule, matter, round, attentionCount);
    }

    private async Task<int> StageAskCardsAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        JsonSerializerOptions jsonOptions,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotReceipt receipt,
        CancellationToken cancellationToken
    )
    {
        int attentionCount = 0;
        foreach (TaskPilotReceiptAsk ask in receipt.Ask)
        {
            if (await StageAskCardAsync(dbContext, context, jsonOptions, rule, matter, round, ask, cancellationToken))
                attentionCount++;
        }

        return attentionCount;
    }

    private async Task<bool> StageAskCardAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        JsonSerializerOptions jsonOptions,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotReceiptAsk ask,
        CancellationToken cancellationToken
    )
    {
        string cardKey = $"ask:{ask.Key}";
        if (await HasOpenAskCardAsync(dbContext, matter, cardKey, cancellationToken))
            return false;

        TaskPilotCard card = new()
        {
            RootId = matter.Id,
            RoundId = round.Id,
            Kind = TaskPilotCardKind.Ask,
            CardKey = cardKey,
            OpenCardKey = cardKey,
            Title = ask.Title,
            Text = ask.Text,
            About = ask.About,
            State = TaskPilotCardState.Open,
            ConversationId = round.ConversationId,
        };
        TaskPilotNotificationService.StageAsync(
            dbContext,
            context,
            card,
            NotificationKind.Question,
            ask.Options,
            ask.Recommend,
            allowText: ask.Options.Count == 0,
            DeserializeChannels(rule, jsonOptions),
            $"card:{card.Id:D}",
            round.HeadEventId
        );
        return true;
    }

    private static Task<bool> HasOpenAskCardAsync(
        TaskPilotDbContext dbContext,
        TaskPilotMatter matter,
        string cardKey,
        CancellationToken cancellationToken
    ) => dbContext.Cards.AnyAsync(card => card.RootId == matter.Id && card.OpenCardKey == cardKey, cancellationToken);

    private int StageNoticeCards(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        JsonSerializerOptions jsonOptions,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotReceipt receipt
    )
    {
        int attentionCount = 0;
        for (int index = 0; index < receipt.Notice.Count; index++)
        {
            TaskPilotReceiptNotice notice = receipt.Notice[index];
            string cardKey = $"notice:{round.Id:D}:{index}";
            bool closed = notice.Fyi;
            TaskPilotCard card = new()
            {
                RootId = matter.Id,
                RoundId = round.Id,
                Kind = TaskPilotCardKind.Notice,
                CardKey = cardKey,
                OpenCardKey = closed ? null : cardKey,
                Title = notice.Title,
                Text = notice.Text,
                State = closed ? TaskPilotCardState.Closed : TaskPilotCardState.Open,
                ResolvedReason = closed ? "This notice was marked FYI." : null,
                ConversationId = round.ConversationId,
            };
            TaskPilotNotificationService.StageAsync(
                dbContext,
                context,
                card,
                NotificationKind.Notice,
                [],
                null,
                allowText: false,
                DeserializeChannels(rule, jsonOptions),
                $"card:{card.Id:D}",
                round.HeadEventId
            );
            attentionCount++;
        }

        return attentionCount;
    }

    private async Task ResolveReceiptCardsAsync(
        TaskPilotDbContext dbContext,
        JsonSerializerOptions jsonOptions,
        TaskPilotMatter matter,
        TaskPilotReceipt receipt,
        CancellationToken cancellationToken
    )
    {
        foreach (TaskPilotReceiptResolve resolve in receipt.Resolve)
        {
            if (!Guid.TryParse(resolve.Card, out Guid cardId))
            {
                continue;
            }
            TaskPilotCard? card = await dbContext.Cards.SingleOrDefaultAsync(
                row => row.Id == cardId && row.RootId == matter.Id && row.State == TaskPilotCardState.Open,
                cancellationToken
            );
            if (card is null)
            {
                continue;
            }

            card.State = TaskPilotCardState.Resolved;
            card.OpenCardKey = null;
            card.ResolvedReason = resolve.Reason;
            card.Version++;
            TaskPilotNotificationService.StageUpdateAsync(dbContext, jsonOptions, card);
        }
    }

    private async Task StageReceiptReviewsAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotReceipt receipt,
        CancellationToken cancellationToken
    )
    {
        foreach (TaskPilotReceiptLater later in receipt.Later)
        {
            await StageReviewAsync(dbContext, context, matter, round, later, cancellationToken);
        }
    }

    private void EnqueueReceiptEvents(
        TaskPilotDbContext dbContext,
        JsonSerializerOptions jsonOptions,
        TaskPilotRule rule,
        TaskPilotRound round,
        TaskPilotReceipt receipt
    )
    {
        foreach (TaskPilotReceiptEvent emitted in receipt.Events)
        {
            JsonElement detail = emitted.Detail ?? JsonDocument.Parse("null").RootElement.Clone();
            var draft = new CloudEventDraft
            {
                Id = $"round:{round.Id:D}:{emitted.Id}",
                Type = emitted.Type,
                Source = $"/agw/extensions/{TaskPilotExtension.ExtensionId:D}/rules/{rule.Id:D}",
                Time = _timeProvider.GetUtcNow(),
                Subject = emitted.Subject,
                Scope = emitted.Scope,
                Tags = emitted.Tags,
                Summary = emitted.Summary,
                Link = emitted.Link,
                DataContentType = "application/json",
                Data = new CloudEventData
                {
                    Kind = CloudEventDataKind.Json,
                    Text = detail.GetRawText(),
                    Json = detail,
                },
                ParentEventId = round.HeadEventId,
            };
            ExtensionOutbox.Enqueue(
                dbContext,
                ExtensionOutboxKind.PublishEvent,
                $"taskpilot:round:{round.Id:D}:event:{emitted.Id}",
                draft,
                jsonOptions
            );
        }
    }

    private void StageReceiptSummary(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        JsonSerializerOptions jsonOptions,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        int attentionCount
    )
    {
        if (
            rule.NotifyPolicy == TaskPilotNotifyPolicy.EveryRound
            || rule.NotifyPolicy == TaskPilotNotifyPolicy.OnAttention && attentionCount > 0
        )
        {
            string cardKey = $"summary:{round.Id:D}";
            TaskPilotCard summary = new()
            {
                RootId = matter.Id,
                RoundId = round.Id,
                Kind = TaskPilotCardKind.Notice,
                CardKey = cardKey,
                OpenCardKey = null,
                Title = "TaskPilot round completed",
                Text = round.Summary ?? "TaskPilot completed a round.",
                State = TaskPilotCardState.Closed,
                ResolvedReason = "Round summary notification.",
                ConversationId = round.ConversationId,
            };
            TaskPilotNotificationService.StageAsync(
                dbContext,
                context,
                summary,
                NotificationKind.Notice,
                [],
                null,
                allowText: false,
                DeserializeChannels(rule, jsonOptions),
                $"card:{summary.Id:D}",
                round.HeadEventId
            );
        }
    }

    private static string? ValidateReceiptSemantics(
        TaskPilotRule rule,
        TaskPilotReceipt receipt,
        JsonSerializerOptions jsonOptions
    )
    {
        if (receipt.Ask.Select(ask => ask.Key).Distinct(StringComparer.Ordinal).Count() != receipt.Ask.Count)
        {
            return "The execution receipt contains duplicate question keys.";
        }

        if (receipt.Ask.Any(ask => ask.Recommend is { } recommend && recommend >= ask.Options.Count))
        {
            return "The execution receipt contains a question recommendation outside its options.";
        }

        if (
            receipt.Resolve.Select(resolve => resolve.Card).Distinct(StringComparer.Ordinal).Count()
            != receipt.Resolve.Count
        )
        {
            return "The execution receipt contains duplicate card resolutions.";
        }

        if (receipt.Resolve.Any(resolve => !Guid.TryParse(resolve.Card, out _)))
        {
            return "The execution receipt contains an invalid card identifier.";
        }

        if (receipt.Later.Select(later => later.Key).Distinct(StringComparer.Ordinal).Count() != receipt.Later.Count)
        {
            return "The execution receipt contains duplicate review keys.";
        }

        if (
            receipt.Events.Select(emitted => emitted.Id).Distinct(StringComparer.Ordinal).Count()
            != receipt.Events.Count
        )
        {
            return "The execution receipt contains duplicate event identifiers.";
        }

        string[] allowedTypes = JsonSerializer.Deserialize<string[]>(rule.EmitTypesJson, jsonOptions) ?? [];
        if (
            receipt.Events.Any(emitted =>
                !emitted.Type.StartsWith("taskpilot.", StringComparison.Ordinal)
                || !allowedTypes.Contains(emitted.Type, StringComparer.Ordinal)
            )
        )
        {
            return "The execution receipt contains an event type that this rule cannot publish.";
        }

        return null;
    }

    private async Task StageReviewAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotReceiptLater later,
        CancellationToken cancellationToken
    )
    {
        TaskPilotReview? review = await dbContext.Reviews.SingleOrDefaultAsync(
            row => row.RootId == matter.Id && row.ReviewKey == later.Key,
            cancellationToken
        );
        if (review is null)
        {
            review = new TaskPilotReview
            {
                RootId = matter.Id,
                ReviewKey = later.Key,
                State = TaskPilotReviewState.Cancelled,
            };
            dbContext.Reviews.Add(review);
        }

        review.RequestSeq++;
        review.About = later.About;
        review.ChainId = round.ChainId;
        review.ParentEventId = round.HeadEventId;
        review.Hops = round.HeadHops;
        review.Version++;
        string businessKey = $"review:{matter.Id:D}:{later.Key}";
        if (later.Cancel)
        {
            review.State = TaskPilotReviewState.Cancelled;
            review.DueAt = null;
            TaskPilotRoundTransitions.StageCancelSchedule(dbContext, context.JsonOptions, review);
            return;
        }

        if (later.At is not { } dueAt)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        review.State = TaskPilotReviewState.Scheduled;
        review.DueAt = dueAt;
        var eventData = new TaskPilotReviewDueData
        {
            MatterId = matter.Id,
            ReviewId = review.Id,
            RequestSeq = review.RequestSeq,
        };
        JsonElement json = JsonSerializer.SerializeToElement(
            eventData,
            TaskPilotJsonContext.Default.TaskPilotReviewDueData
        );
        var draft = new CloudEventDraft
        {
            Id = $"{review.Id:D}:due:{review.RequestSeq}",
            Type = "taskpilot.review.due",
            Source = $"/agw/extensions/{TaskPilotExtension.ExtensionId:D}/reviews/{review.Id:D}",
            Subject = matter.Id.ToString("D"),
            Time = _timeProvider.GetUtcNow(),
            DataContentType = "application/json",
            Data = new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = json.GetRawText(),
                Json = json,
            },
            ParentEventId = round.HeadEventId,
        };
        var request = new ScheduleRequest
        {
            BusinessKey = businessKey,
            RequestSeq = review.RequestSeq,
            Kind = ScheduleKind.Once,
            At = dueAt,
            TimeZone = "UTC",
            Event = draft,
            ParentEventId = round.HeadEventId,
        };
        review.ScheduleOutboxId = ExtensionOutbox.Enqueue(
            dbContext,
            ExtensionOutboxKind.UpsertSchedule,
            $"taskpilot:review:{review.Id:D}:schedule:{review.RequestSeq}",
            request,
            context.JsonOptions
        );
    }

    private static TaskPilotRound NewScheduledRound(
        TaskPilotMatter matter,
        TaskPilotRule rule,
        ExtensionEventDelivery delivery,
        TaskPilotRoundTrigger trigger
    ) =>
        new()
        {
            RootId = matter.Id,
            RuleVersion = rule.Version,
            State = TaskPilotRoundState.Queued,
            Trigger = trigger,
            HeadEventId = delivery.EventId,
            ChainId = delivery.ChainId,
            HeadHops = delivery.Hops,
            ParentEventId = delivery.ParentEventId,
            Version = 0,
        };

    private static TaskPilotRoundInput NewScheduledInput(
        TaskPilotRound round,
        ExtensionEventDelivery delivery,
        TaskPilotRoundInputKind kind,
        string summary
    ) =>
        new()
        {
            RootId = round.Id,
            EventId = delivery.EventId,
            Kind = kind,
            PayloadJson = delivery.Event.Data is { Kind: CloudEventDataKind.Json, Json: { } data }
                ? data.GetRawText()
                : null,
            EventJson = JsonSerializer.Serialize(delivery.Event, TaskPilotJsonContext.Default.CloudEventEnvelope),
            ReceivedAt = delivery.ReceivedAt,
            ChainId = delivery.ChainId,
            ParentEventId = delivery.ParentEventId,
            Hops = delivery.Hops,
            Summary = summary,
            Link = delivery.Link,
        };

    private static async Task<bool> HasInputAsync(
        TaskPilotDbContext dbContext,
        Guid eventId,
        CancellationToken cancellationToken
    ) => await dbContext.RoundInputs.AnyAsync(row => row.EventId == eventId, cancellationToken);

    private static bool TryReadEventData<T>(ExtensionEventDelivery delivery, [NotNullWhen(true)] out T? value)
        where T : class
    {
        value = null;
        if (delivery.Event.Data is not { Kind: CloudEventDataKind.Json, Json: { } data })
        {
            return false;
        }

        value = JsonSerializer.Deserialize<T>(data, TaskPilotJsonContext.Default.Options);
        return value is not null;
    }

    private static bool TryReadRoundCorrelation(string correlationKey, out Guid roundId, out int attempt)
    {
        roundId = Guid.Empty;
        attempt = 0;
        string[] parts = correlationKey.Split(':', 3);
        return parts.Length == 3
            && parts[0] == "round"
            && Guid.TryParse(parts[1], out roundId)
            && int.TryParse(parts[2], out attempt)
            && attempt > 0
            && correlationKey == $"round:{roundId:D}:{attempt}";
    }

    private static bool TryString(JsonElement data, string property, out string value)
    {
        value = string.Empty;
        if (!data.TryGetProperty(property, out JsonElement item) || item.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = item.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGuid(JsonElement data, string property, out Guid value)
    {
        value = Guid.Empty;
        return data.TryGetProperty(property, out JsonElement item)
            && item.ValueKind == JsonValueKind.String
            && item.TryGetGuid(out value);
    }

    private static bool TryInt64(JsonElement data, string property, out long value)
    {
        value = 0;
        return data.TryGetProperty(property, out JsonElement item)
            && item.ValueKind == JsonValueKind.Number
            && item.TryGetInt64(out value)
            && value >= 0;
    }

    private static string[] DeserializeChannels(TaskPilotRule rule, JsonSerializerOptions jsonOptions) =>
        JsonSerializer.Deserialize<string[]>(rule.ChannelsJson, jsonOptions) ?? [];
}
