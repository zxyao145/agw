using System.Text.Json;
using Agw.Events.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Notifications.Contracts;
using Agw.Shared.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace Agw.Extensions.TaskPilot;

internal static class TaskPilotRoundTransitions
{
    public static async Task FailAsync(
        TaskPilotDbContext dbContext,
        JsonSerializerOptions jsonOptions,
        TimeProvider timeProvider,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        TaskPilotRoundAttempt? attempt,
        string? error,
        bool stageWake,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        round.State = TaskPilotRoundState.Failed;
        round.Error = string.IsNullOrWhiteSpace(error) ? "TaskPilot execution failed." : error;
        round.Outcome = TaskPilotRoundOutcome.RetryableFailure;
        round.EndedAt = now;
        round.Version++;
        if (attempt is not null)
        {
            attempt.State = TaskPilotAttemptState.Failed;
            attempt.Error ??= round.Error;
            attempt.EndedAt ??= now;
        }

        matter.ConsecutiveFailures++;
        matter.RetryRequestSeq++;
        matter.Version++;
        TaskPilotRetryPolicy retry =
            JsonSerializer.Deserialize<TaskPilotRetryPolicy>(rule.RetryJson, jsonOptions) ?? new();
        matter.State =
            matter.ConsecutiveFailures >= retry.StopThreshold ? TaskPilotMatterState.Stopped
            : matter.State == TaskPilotMatterState.Paused ? TaskPilotMatterState.Paused
            : TaskPilotMatterState.Active;

        if (matter.State != TaskPilotMatterState.Stopped)
        {
            int index = Math.Min(matter.ConsecutiveFailures - 1, retry.DelayMinutes.Count - 1);
            int delayMinutes = Math.Min(retry.DelayMinutes[index], retry.MaximumDelayMinutes);
            StageRetry(dbContext, jsonOptions, matter, round, now, delayMinutes);
        }

        if (matter.ConsecutiveFailures >= retry.NotificationThreshold)
        {
            await StageFailureNoticeAsync(dbContext, jsonOptions, rule, matter, round, cancellationToken);
        }

        await ReleaseOccupancyAsync(dbContext, round.Id, cancellationToken);
        TaskPilotTelemetry.RecordRoundTransition(round.State);
        if (stageWake)
        {
            StageWake(dbContext, jsonOptions, matter, round, now);
        }
    }

    public static async Task CompleteAsync(
        TaskPilotDbContext dbContext,
        JsonSerializerOptions jsonOptions,
        TimeProvider timeProvider,
        TaskPilotMatter matter,
        TaskPilotRound round,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        round.State = TaskPilotRoundState.Completed;
        round.Error = null;
        round.ReceiptError = null;
        round.EndedAt = now;
        round.Version++;
        matter.ConsecutiveFailures = 0;
        matter.RetryRequestSeq++;
        matter.Version++;

        bool waitingForHuman = await dbContext.Cards.AnyAsync(
            card =>
                card.RootId == matter.Id && card.Kind == TaskPilotCardKind.Ask && card.State == TaskPilotCardState.Open,
            cancellationToken
        );
        if (matter.State is not (TaskPilotMatterState.Paused or TaskPilotMatterState.Stopped))
        {
            matter.State = waitingForHuman ? TaskPilotMatterState.WaitingHuman : TaskPilotMatterState.Active;
        }

        await CloseFailureNoticeAsync(dbContext, jsonOptions, matter, cancellationToken);
        await ReleaseOccupancyAsync(dbContext, round.Id, cancellationToken);
        TaskPilotTelemetry.RecordRoundTransition(round.State);
    }

    public static async Task FailDeliveryReviewAsync(
        TaskPilotDbContext dbContext,
        TimeProvider timeProvider,
        TaskPilotRound round,
        TaskPilotRoundAttempt attempt,
        string error,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        round.State = TaskPilotRoundState.Failed;
        round.Error = string.IsNullOrWhiteSpace(error) ? "The TaskPilot delivery review failed." : error;
        round.Outcome = TaskPilotRoundOutcome.RetryableFailure;
        round.EndedAt = now;
        round.Version++;
        attempt.State = TaskPilotAttemptState.Failed;
        attempt.Error = round.Error;
        attempt.EndedAt ??= now;
        await ReleaseOccupancyAsync(dbContext, round.Id, cancellationToken);
        TaskPilotTelemetry.RecordRoundTransition(round.State);
    }

    public static async Task ReleaseOccupancyAsync(
        TaskPilotDbContext dbContext,
        Guid roundId,
        CancellationToken cancellationToken
    )
    {
        TaskPilotMatterActivity[] activities = await dbContext
            .MatterActivities.Where(activity => activity.RoundId == roundId)
            .ToArrayAsync(cancellationToken);
        TaskPilotConcurrencySlot[] slots = await dbContext
            .ConcurrencySlots.Where(slot => slot.RoundId == roundId)
            .ToArrayAsync(cancellationToken);
        dbContext.MatterActivities.RemoveRange(activities);
        dbContext.ConcurrencySlots.RemoveRange(slots);
    }

    public static Guid StageCancelSchedule(
        TaskPilotDbContext dbContext,
        JsonSerializerOptions jsonOptions,
        TaskPilotReview review
    )
    {
        string businessKey = $"review:{review.RootId:D}:{review.ReviewKey}";
        Guid outboxId = ExtensionOutbox.Enqueue(
            dbContext,
            ExtensionOutboxKind.CancelSchedule,
            $"taskpilot:review:{review.Id:D}:cancel:{review.RequestSeq}",
            new TaskPilotCancelReviewScheduleRequest { BusinessKey = businessKey, RequestSeq = review.RequestSeq },
            jsonOptions
        );
        review.ScheduleOutboxId = outboxId;
        return outboxId;
    }

    private static void StageRetry(
        TaskPilotDbContext dbContext,
        JsonSerializerOptions jsonOptions,
        TaskPilotMatter matter,
        TaskPilotRound round,
        DateTimeOffset now,
        int delayMinutes
    )
    {
        var data = new TaskPilotRetryDueData
        {
            MatterId = matter.Id,
            RoundId = round.Id,
            RequestSeq = matter.RetryRequestSeq,
        };
        JsonElement json = JsonSerializer.SerializeToElement(data, TaskPilotJsonContext.Default.TaskPilotRetryDueData);
        var draft = new CloudEventDraft
        {
            Id = $"{round.Id:D}:retry:{matter.RetryRequestSeq}",
            Type = "taskpilot.round.retry",
            Source = $"/agw/extensions/{TaskPilotExtension.ExtensionId:D}/rounds/{round.Id:D}",
            Time = now,
            Subject = matter.Id.ToString("D"),
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
            BusinessKey = $"retry:{matter.Id:D}",
            RequestSeq = matter.RetryRequestSeq,
            Kind = ScheduleKind.Once,
            At = now.AddMinutes(delayMinutes),
            TimeZone = "UTC",
            Event = draft,
            ParentEventId = round.HeadEventId,
        };
        ExtensionOutbox.Enqueue(
            dbContext,
            ExtensionOutboxKind.UpsertSchedule,
            $"taskpilot:retry:{matter.Id:D}:{matter.RetryRequestSeq}",
            request,
            jsonOptions
        );
    }

    private static void StageWake(
        TaskPilotDbContext dbContext,
        JsonSerializerOptions jsonOptions,
        TaskPilotMatter matter,
        TaskPilotRound round,
        DateTimeOffset now
    )
    {
        var data = new TaskPilotRetryDueData
        {
            MatterId = matter.Id,
            RoundId = round.Id,
            RequestSeq = matter.Version,
            WakeOnly = true,
        };
        JsonElement json = JsonSerializer.SerializeToElement(data, TaskPilotJsonContext.Default.TaskPilotRetryDueData);
        var draft = new CloudEventDraft
        {
            Id = $"{round.Id:D}:wake:{matter.Version}",
            Type = "taskpilot.round.retry",
            Source = $"/agw/extensions/{TaskPilotExtension.ExtensionId:D}/rounds/{round.Id:D}",
            Time = now,
            Subject = matter.Id.ToString("D"),
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
            BusinessKey = $"wake:{matter.Id:D}",
            RequestSeq = matter.Version,
            Kind = ScheduleKind.Once,
            At = now,
            TimeZone = "UTC",
            Event = draft,
            ParentEventId = round.HeadEventId,
        };
        ExtensionOutbox.Enqueue(
            dbContext,
            ExtensionOutboxKind.UpsertSchedule,
            $"taskpilot:wake:{matter.Id:D}:{matter.Version}",
            request,
            jsonOptions
        );
    }

    private static async Task StageFailureNoticeAsync(
        TaskPilotDbContext dbContext,
        JsonSerializerOptions jsonOptions,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotRound round,
        CancellationToken cancellationToken
    )
    {
        TaskPilotCard? card = matter.FailureCardId is { } id
            ? await dbContext.Cards.SingleOrDefaultAsync(row => row.Id == id, cancellationToken)
            : null;
        string text =
            matter.State == TaskPilotMatterState.Stopped
                ? $"The matter stopped after {matter.ConsecutiveFailures} consecutive failures. {round.Error}"
                : $"The matter failed {matter.ConsecutiveFailures} consecutive times. {round.Error}";
        if (card is { State: TaskPilotCardState.Open })
        {
            card.Text = text;
            card.Version++;
            TaskPilotNotificationService.StageUpdateAsync(dbContext, jsonOptions, card);
            return;
        }

        card = new TaskPilotCard
        {
            RootId = matter.Id,
            RoundId = round.Id,
            Kind = TaskPilotCardKind.Notice,
            CardKey = $"failure:{matter.Id:D}",
            OpenCardKey = $"failure:{matter.Id:D}",
            Title = matter.State == TaskPilotMatterState.Stopped ? "TaskPilot stopped" : "TaskPilot needs attention",
            Text = text,
            State = TaskPilotCardState.Open,
        };
        matter.FailureCardId = card.Id;
        TaskPilotNotificationService.StageAsync(
            dbContext,
            jsonOptions,
            card,
            NotificationKind.Notice,
            [],
            null,
            allowText: false,
            JsonSerializer.Deserialize<string[]>(rule.ChannelsJson, jsonOptions) ?? [],
            $"card:{card.Id:D}",
            round.HeadEventId
        );
        matter.Version++;
    }

    private static async Task CloseFailureNoticeAsync(
        TaskPilotDbContext dbContext,
        JsonSerializerOptions jsonOptions,
        TaskPilotMatter matter,
        CancellationToken cancellationToken
    )
    {
        if (matter.FailureCardId is not { } cardId)
        {
            return;
        }

        TaskPilotCard? card = await dbContext.Cards.SingleOrDefaultAsync(row => row.Id == cardId, cancellationToken);
        matter.FailureCardId = null;
        if (card is not { State: TaskPilotCardState.Open })
        {
            return;
        }

        card.State = TaskPilotCardState.Closed;
        card.OpenCardKey = null;
        card.ResolvedReason = "The matter completed successfully.";
        card.Version++;
        TaskPilotNotificationService.StageUpdateAsync(dbContext, jsonOptions, card);
    }
}
