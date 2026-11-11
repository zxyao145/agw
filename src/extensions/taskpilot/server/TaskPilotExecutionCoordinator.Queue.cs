using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Agents.Contracts.Execution;
using Agw.Agents.Contracts.Messages;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Json.Logic;
using Microsoft.EntityFrameworkCore;

namespace Agw.Extensions.TaskPilot;

internal sealed partial class TaskPilotExecutionCoordinator
{
    private readonly TimeProvider _timeProvider;

    public TaskPilotExecutionCoordinator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public async ValueTask<ExtensionEventHandlerResult> EnqueueEventAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotRule rule,
        ExtensionEventDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        if (delivery.Hops < 0)
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        string? matterKey = EvaluateMatterKey(rule, delivery.Event);
        if (string.IsNullOrWhiteSpace(matterKey) || matterKey.Length > 500)
        {
            return ExtensionEventHandlerResult.Failed("The TaskPilot matter key is empty or too long.");
        }

        TaskPilotMatter? matter = await dbContext.Matters.SingleOrDefaultAsync(
            row => row.RootId == rule.Id && row.MatterKey == matterKey,
            cancellationToken
        );
        if (matter is null)
        {
            matter = new TaskPilotMatter
            {
                RootId = rule.Id,
                MatterKey = matterKey,
                Title = delivery.Summary ?? delivery.Event.Subject ?? rule.Name,
                Link = delivery.Link,
                State = TaskPilotMatterState.Active,
                ChainId = delivery.ChainId,
                Version = 0,
            };
            dbContext.Matters.Add(matter);
        }

        bool alreadyAdded = await (
            from input in dbContext.RoundInputs
            join inputRound in dbContext.Rounds on input.RootId equals inputRound.Id
            where inputRound.RootId == matter.Id && input.EventId == delivery.EventId
            select input.Id
        ).AnyAsync(cancellationToken);
        if (alreadyAdded)
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        TaskPilotRound? round = await dbContext
            .Rounds.Where(row => row.RootId == matter.Id && row.State == TaskPilotRoundState.Queued)
            .OrderBy(row => row.CreateTime)
            .FirstOrDefaultAsync(cancellationToken);
        if (round is null)
        {
            round = new TaskPilotRound
            {
                RootId = matter.Id,
                RuleVersion = rule.Version,
                State = TaskPilotRoundState.Queued,
                Trigger = TaskPilotRoundTrigger.Event,
                Version = 0,
            };
            dbContext.Rounds.Add(round);
        }

        dbContext.RoundInputs.Add(
            new TaskPilotRoundInput
            {
                RootId = round.Id,
                EventId = delivery.EventId,
                Kind = TaskPilotRoundInputKind.Event,
                EventJson = JsonSerializer.Serialize(delivery.Event, context.JsonOptions),
                ReceivedAt = delivery.ReceivedAt,
                ChainId = delivery.ChainId,
                ParentEventId = delivery.ParentEventId,
                Hops = delivery.Hops,
                Summary = delivery.Summary,
                Link = delivery.Link,
            }
        );
        matter.Title = delivery.Summary ?? delivery.Event.Subject ?? matter.Title;
        matter.Link = delivery.Link ?? matter.Link;
        matter.Version++;

        await dbContext.SaveChangesAsync(cancellationToken);
        await StartRunnableAsync(dbContext, context, cancellationToken);
        return ExtensionEventHandlerResult.Succeeded;
    }

    public async Task StartRunnableAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        CancellationToken cancellationToken
    )
    {
        await dbContext.SaveChangesAsync(cancellationToken);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var candidates = await (
            from round in dbContext.Rounds
            join matter in dbContext.Matters on round.RootId equals matter.Id
            join rule in dbContext.Rules on matter.RootId equals rule.Id
            where
                round.State == TaskPilotRoundState.Queued
                && (round.NotBefore == null || round.NotBefore <= now)
                && rule.Enabled
                && rule.Kind == TaskPilotRuleKind.Agent
                && !rule.Paused
                && rule.DeletedAt == null
                && matter.State != TaskPilotMatterState.Paused
                && matter.State != TaskPilotMatterState.Stopped
            orderby round.CreateTime
            select new
            {
                RoundId = round.Id,
                MatterId = matter.Id,
                RuleId = rule.Id,
            }
        ).ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return;
        }

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
            TaskPilotRule rule = await dbContext.Rules.SingleAsync(
                row => row.Id == candidate.RuleId,
                cancellationToken
            );
            TaskPilotRuleRevision? revision = await dbContext.RuleRevisions.SingleOrDefaultAsync(
                row => row.RootId == rule.Id && row.Version == round.RuleVersion,
                cancellationToken
            );
            if (revision is null)
            {
                await TaskPilotRoundTransitions.FailAsync(
                    dbContext,
                    context.JsonOptions,
                    _timeProvider,
                    rule,
                    matter,
                    round,
                    attempt: null,
                    error: "The TaskPilot rule revision for this queued round is missing.",
                    stageWake: false,
                    cancellationToken
                );
                continue;
            }

            TaskPilotRuleDetails snapshot =
                JsonSerializer.Deserialize(revision.SnapshotJson, TaskPilotJsonContext.Default.TaskPilotRuleDetails)
                ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid);

            IReadOnlyList<TaskPilotRoundInput> inputs = await dbContext
                .RoundInputs.Where(input => input.RootId == round.Id)
                .OrderBy(input => input.ReceivedAt)
                .ToListAsync(cancellationToken);
            IReadOnlyList<TaskPilotCard> cards = await dbContext
                .Cards.Where(card => card.RootId == matter.Id)
                .OrderBy(card => card.CreateTime)
                .ToListAsync(cancellationToken);
            IReadOnlyList<TaskPilotReview> reviews = await dbContext
                .Reviews.Where(review => review.RootId == matter.Id)
                .OrderBy(review => review.DueAt)
                .ToListAsync(cancellationToken);
            Guid turnId = Guid.CreateVersion7();
            TaskPilotInputBuildResult built = TaskPilotInputBuilder.Build(
                snapshot,
                matter,
                round,
                inputs,
                cards,
                reviews,
                turnId,
                _timeProvider
            );
            if (built.Error is { } inputError)
            {
                await TaskPilotRoundTransitions.FailAsync(
                    dbContext,
                    context.JsonOptions,
                    _timeProvider,
                    rule,
                    matter,
                    round,
                    attempt: null,
                    error: inputError,
                    stageWake: false,
                    cancellationToken
                );
                continue;
            }

            await TrySubmitSpecificAsync(
                dbContext,
                context,
                snapshot,
                matter,
                round,
                built.Input!,
                $"round:{round.Id:D}:{round.Attempt + 1}",
                round.HeadEventId,
                BackgroundToolPolicy.Default,
                TaskPilotAttemptPurpose.Run,
                cancellationToken
            );
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal async Task<TaskPilotRoundAttempt?> TrySubmitSpecificAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        TaskPilotRuleDetails snapshot,
        TaskPilotMatter matter,
        TaskPilotRound round,
        AgwUserInput input,
        string correlationKey,
        Guid? parentEventId,
        BackgroundToolPolicy toolPolicy,
        TaskPilotAttemptPurpose purpose,
        CancellationToken cancellationToken
    )
    {
        if (round.State != TaskPilotRoundState.Queued || snapshot.MaxConcurrency < 1)
        {
            return null;
        }

        if (await dbContext.MatterActivities.AnyAsync(activity => activity.RootId == matter.Id, cancellationToken))
        {
            return null;
        }

        HashSet<int> occupied = await dbContext
            .ConcurrencySlots.Where(slot => slot.GroupKey == snapshot.GroupKey)
            .Select(slot => slot.SlotIndex)
            .ToHashSetAsync(cancellationToken);
        int slotIndex = Enumerable
            .Range(0, snapshot.MaxConcurrency)
            .FirstOrDefault(index => !occupied.Contains(index), -1);
        if (slotIndex < 0)
        {
            return null;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        Guid turnId = Guid.CreateVersion7();
        round.Attempt++;
        round.TurnId = turnId;
        round.State = TaskPilotRoundState.Submitting;
        round.StartedAt = now;
        round.Version++;
        matter.Version++;
        var request = new BackgroundExecutionRequest(
            turnId,
            new AgentTarget(snapshot.TargetKind, snapshot.TargetId),
            snapshot.ProjectId,
            ResolveConversationId(snapshot, matter, round),
            matter.Title,
            input,
            snapshot.PermissionMode,
            toolPolicy,
            correlationKey,
            parentEventId
        );
        Guid outboxId = ExtensionOutbox.Enqueue(
            dbContext,
            ExtensionOutboxKind.SubmitExecution,
            SubmitIdempotencyKey(round.Id, round.Attempt),
            request,
            context.JsonOptions
        );
        var attempt = new TaskPilotRoundAttempt
        {
            RootId = round.Id,
            Attempt = round.Attempt,
            TurnId = turnId,
            Purpose = purpose,
            State = TaskPilotAttemptState.Submitting,
            SubmitOutboxId = outboxId,
            StartedAt = now,
        };
        dbContext.RoundAttempts.Add(attempt);
        dbContext.MatterActivities.Add(
            new TaskPilotMatterActivity
            {
                RootId = matter.Id,
                RoundId = round.Id,
                AcquiredAt = now,
            }
        );
        dbContext.ConcurrencySlots.Add(
            new TaskPilotConcurrencySlot
            {
                GroupKey = snapshot.GroupKey,
                SlotIndex = slotIndex,
                RoundId = round.Id,
            }
        );
        await dbContext.SaveChangesAsync(cancellationToken);
        return attempt;
    }

    private static Guid? ResolveConversationId(
        TaskPilotRuleDetails rule,
        TaskPilotMatter matter,
        TaskPilotRound round
    ) =>
        round.ConversationId ?? (rule.SessionPolicy == TaskPilotSessionPolicy.PerMatter ? matter.ConversationId : null);

    private static string SubmitIdempotencyKey(Guid roundId, int attempt) =>
        $"taskpilot:round:{roundId:D}:attempt:{attempt}:submit";

    private static string? EvaluateMatterKey(TaskPilotRule rule, CloudEventEnvelope cloudEvent)
    {
        if (
            string.IsNullOrWhiteSpace(rule.MatterKeyJson)
            || string.Equals(rule.MatterKeyJson, "null", StringComparison.Ordinal)
        )
        {
            return cloudEvent.Subject;
        }

        JsonNode? expression = JsonNode.Parse(rule.MatterKeyJson);
        JsonNode? value = JsonLogic.Apply(expression, CloudEventJsonLogicContext.Create(cloudEvent));
        if (value is JsonValue jsonValue && jsonValue.TryGetValue(out string? text))
        {
            return text;
        }

        return value?.ToJsonString();
    }
}
