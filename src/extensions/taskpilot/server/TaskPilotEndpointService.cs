using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Contracts.Execution;
using Agw.Events.Contracts;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Shared.Exceptions;
using Json.Logic;
using Json.Path;
using Microsoft.EntityFrameworkCore;

namespace Agw.Extensions.TaskPilot;

internal sealed class TaskPilotEndpointService
{
    private readonly TaskPilotDbContext _dbContext;
    private readonly TaskPilotExecutionCoordinator _execution;
    private readonly TimeProvider _timeProvider;

    public TaskPilotEndpointService(
        TaskPilotDbContext dbContext,
        TaskPilotExecutionCoordinator execution,
        TimeProvider timeProvider
    )
    {
        _dbContext = dbContext;
        _execution = execution;
        _timeProvider = timeProvider;
    }

    public async ValueTask<ExtensionEndpointResponse> HandleAsync(
        string endpointId,
        IExtensionContext context,
        ExtensionEndpointRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Method == "GET")
        {
            return await HandleQueryAsync(endpointId, context, request, cancellationToken);
        }

        ExtensionCaller caller = TaskPilotCaller.Require(context);
        await using IExtensionLease lease = await context.Leases.AcquireAsync(
            TaskPilotCaller.MutationLeaseName(caller.UserId!),
            cancellationToken
        );
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lease.HandleLostToken
        );
        CancellationToken token = linkedCancellation.Token;
        await TaskPilotSystemSubscription.EnsureAsync(_dbContext, context, token);
        ExtensionEndpointResponse response = await HandleMutationAsync(endpointId, context, request, token);
        lease.HandleLostToken.ThrowIfCancellationRequested();
        await _dbContext.SaveChangesAsync(token);
        lease.HandleLostToken.ThrowIfCancellationRequested();
        return response;
    }

    private async Task<ExtensionEndpointResponse> HandleQueryAsync(
        string endpointId,
        IExtensionContext context,
        ExtensionEndpointRequest request,
        CancellationToken cancellationToken
    )
    {
        return endpointId switch
        {
            "rules-list" => JsonResponse(
                (await _dbContext.Rules.OrderBy(rule => rule.CreateTime).ToArrayAsync(cancellationToken))
                    .Select(TaskPilotRuleMapping.ToSummary)
                    .ToArray(),
                context.JsonOptions
            ),
            "rules-get" => await GetRuleAsync(context, request, cancellationToken),
            "matters-list" => await ListMattersAsync(context, request, cancellationToken),
            "matters-get" => await GetMatterAsync(context, request, cancellationToken),
            "rounds-list" => JsonResponse(
                (
                    await _dbContext
                        .Rounds.Where(round => round.RootId == RequiredQueryGuid(request, "matterId"))
                        .OrderByDescending(round => round.CreateTime)
                        .ToArrayAsync(cancellationToken)
                )
                    .Select(ToRoundSummary)
                    .ToArray(),
                context.JsonOptions
            ),
            "rounds-get" => await GetRoundAsync(context, request, cancellationToken),
            "reviews-list" => JsonResponse(
                (
                    await _dbContext
                        .Reviews.Where(review => review.RootId == RequiredQueryGuid(request, "matterId"))
                        .OrderBy(review => review.DueAt)
                        .ToArrayAsync(cancellationToken)
                )
                    .Select(ToReviewSummary)
                    .ToArray(),
                context.JsonOptions
            ),
            "deliveries-get" => JsonResponse(
                (
                    await _dbContext
                        .Deliveries.Where(delivery => delivery.RootId == RequiredQueryGuid(request, "matterId"))
                        .OrderByDescending(delivery => delivery.CreateTime)
                        .ToArrayAsync(cancellationToken)
                )
                    .Select(ToDeliverySummary)
                    .ToArray(),
                context.JsonOptions
            ),
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };
    }

    private async Task<ExtensionEndpointResponse> HandleMutationAsync(
        string endpointId,
        IExtensionContext context,
        ExtensionEndpointRequest request,
        CancellationToken cancellationToken
    )
    {
        return endpointId switch
        {
            "rules-create" => await SaveRuleAsync(
                context,
                ReadBody<TaskPilotRuleWriteRequest>(request, context),
                null,
                cancellationToken
            ),
            "rules-update" => await UpdateRuleAsync(
                context,
                ReadBody<TaskPilotRuleWriteRequest>(request, context),
                cancellationToken
            ),
            "rules-delete" => await DeleteRuleAsync(
                context,
                ReadBody<TaskPilotVersionRequest>(request, context),
                cancellationToken
            ),
            "rules-pause" => await SetRulePausedAsync(
                context,
                ReadBody<TaskPilotVersionRequest>(request, context),
                paused: true,
                cancellationToken
            ),
            "rules-resume" => await SetRulePausedAsync(
                context,
                ReadBody<TaskPilotVersionRequest>(request, context),
                paused: false,
                cancellationToken
            ),
            "matters-pause" => await SetMatterStateAsync(
                context,
                ReadBody<TaskPilotVersionRequest>(request, context),
                TaskPilotMatterState.Paused,
                cancellationToken
            ),
            "matters-resume" => await SetMatterStateAsync(
                context,
                ReadBody<TaskPilotVersionRequest>(request, context),
                TaskPilotMatterState.Active,
                cancellationToken
            ),
            "matters-run" => await RunMatterAsync(
                context,
                ReadBody<TaskPilotVersionRequest>(request, context),
                cancellationToken
            ),
            "matters-cancel" => await CancelMatterAsync(
                context,
                ReadBody<TaskPilotMatterCancelRequest>(request, context),
                cancellationToken
            ),
            "reviews-cancel" => await CancelReviewAsync(
                context,
                ReadBody<TaskPilotVersionRequest>(request, context),
                cancellationToken
            ),
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };
    }

    private async Task<ExtensionEndpointResponse> GetRuleAsync(
        IExtensionContext context,
        ExtensionEndpointRequest request,
        CancellationToken cancellationToken
    )
    {
        Guid id = RequiredQueryGuid(request, "id");
        TaskPilotRule rule =
            await _dbContext.Rules.SingleOrDefaultAsync(row => row.Id == id, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        return JsonResponse(TaskPilotRuleMapping.ToDetails(rule, context.JsonOptions), context.JsonOptions);
    }

    private async Task<ExtensionEndpointResponse> ListMattersAsync(
        IExtensionContext context,
        ExtensionEndpointRequest request,
        CancellationToken cancellationToken
    )
    {
        Guid? ruleId = OptionalQueryGuid(request, "ruleId");
        IQueryable<TaskPilotMatter> query = _dbContext.Matters;
        if (ruleId is { } id)
        {
            query = query.Where(matter => matter.RootId == id);
        }

        TaskPilotMatter[] matters = await query
            .OrderByDescending(matter => matter.UpdateTime ?? matter.CreateTime)
            .Take(100)
            .ToArrayAsync(cancellationToken);
        var response = new List<TaskPilotMatterSummary>(matters.Length);
        foreach (TaskPilotMatter matter in matters)
        {
            response.Add(await ToMatterSummaryAsync(matter, cancellationToken));
        }

        return JsonResponse(response.ToArray(), context.JsonOptions);
    }

    private async Task<ExtensionEndpointResponse> GetMatterAsync(
        IExtensionContext context,
        ExtensionEndpointRequest request,
        CancellationToken cancellationToken
    )
    {
        Guid id = RequiredQueryGuid(request, "id");
        TaskPilotMatter matter =
            await _dbContext.Matters.SingleOrDefaultAsync(row => row.Id == id, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        TaskPilotCard[] cards = await _dbContext
            .Cards.Where(card => card.RootId == id)
            .OrderByDescending(card => card.CreateTime)
            .ToArrayAsync(cancellationToken);
        var response = new TaskPilotMatterDetails
        {
            Matter = await ToMatterSummaryAsync(matter, cancellationToken),
            Cards = cards.Select(ToCardSummary).ToArray(),
        };
        return JsonResponse(response, context.JsonOptions);
    }

    private async Task<ExtensionEndpointResponse> GetRoundAsync(
        IExtensionContext context,
        ExtensionEndpointRequest request,
        CancellationToken cancellationToken
    )
    {
        Guid id = RequiredQueryGuid(request, "id");
        TaskPilotRound round =
            await _dbContext.Rounds.SingleOrDefaultAsync(row => row.Id == id, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        TaskPilotRoundInput[] inputs = await _dbContext
            .RoundInputs.Where(input => input.RootId == round.Id)
            .OrderBy(input => input.ReceivedAt)
            .ToArrayAsync(cancellationToken);
        TaskPilotRoundAttempt[] attempts = await _dbContext
            .RoundAttempts.Where(attempt => attempt.RootId == round.Id)
            .OrderBy(attempt => attempt.Attempt)
            .ToArrayAsync(cancellationToken);
        return JsonResponse(
            new TaskPilotRoundDetails
            {
                Round = ToRoundSummary(round),
                Inputs = inputs.Select(ToRoundInputSummary).ToArray(),
                Attempts = attempts.Select(ToRoundAttemptSummary).ToArray(),
                Receipt = TaskPilotRuleMapping.ParseOptionalJson(round.ReceiptJson),
            },
            context.JsonOptions
        );
    }

    private async Task<ExtensionEndpointResponse> SaveRuleAsync(
        IExtensionContext context,
        TaskPilotRuleWriteRequest request,
        TaskPilotRule? rule,
        CancellationToken cancellationToken
    )
    {
        ValidateRule(request);
        await ValidateNoToolsTargetAsync(context, request, cancellationToken);
        bool isNew = rule is null;
        Guid ruleId = rule?.Id ?? Guid.CreateVersion7();
        string groupKey = string.IsNullOrWhiteSpace(request.GroupKey) ? $"rule:{ruleId:D}" : request.GroupKey.Trim();
        bool groupHasOtherConcurrency = await _dbContext.Rules.AnyAsync(
            existing =>
                existing.GroupKey == groupKey
                && existing.Id != ruleId
                && existing.DeletedAt == null
                && existing.MaxConcurrency != request.MaxConcurrency,
            cancellationToken
        );
        if (groupHasOtherConcurrency)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        long version = isNew ? 1 : rule!.Version + 1;
        rule ??= new TaskPilotRule
        {
            Id = ruleId,
            Name = request.Name,
            Version = version,
        };
        rule.Name = request.Name.Trim();
        rule.Kind = request.Kind;
        rule.Enabled = request.Enabled;
        rule.SourceIdsJson = JsonSerializer.Serialize(request.SourceIds, context.JsonOptions);
        rule.EventTypesJson = JsonSerializer.Serialize(request.EventTypes, context.JsonOptions);
        rule.ConditionJson = TaskPilotRuleMapping.SerializeOptionalJson(request.Condition);
        rule.MatterKeyJson = TaskPilotRuleMapping.SerializeOptionalJson(request.MatterKey);
        rule.TargetKind = request.TargetKind;
        rule.TargetId = request.TargetId;
        rule.ProjectId = request.ProjectId;
        rule.Instructions = request.Instructions;
        rule.Skill = request.Skill;
        rule.DataScopeJson = JsonSerializer.Serialize(request.DataScope, context.JsonOptions);
        rule.PermissionMode = request.PermissionMode;
        rule.AskFirstJson = JsonSerializer.Serialize(request.AskFirst, context.JsonOptions);
        rule.GroupKey = groupKey;
        rule.MaxConcurrency = request.MaxConcurrency;
        rule.SessionPolicy = request.SessionPolicy;
        rule.ChannelsJson = JsonSerializer.Serialize(request.Channels, context.JsonOptions);
        rule.NotifyPolicy = request.NotifyPolicy;
        rule.EmitTypesJson = JsonSerializer.Serialize(request.EmitTypes, context.JsonOptions);
        rule.RetryJson = JsonSerializer.Serialize(request.Retry, context.JsonOptions);
        rule.DeliveryJson = TaskPilotRuleMapping.SerializeOptionalJson(request.Delivery);
        rule.Version = version;

        if (isNew)
        {
            _dbContext.Rules.Add(rule);
        }

        TaskPilotRuleDetails details = TaskPilotRuleMapping.ToDetails(rule, context.JsonOptions);
        _dbContext.RuleRevisions.Add(
            new TaskPilotRuleRevision
            {
                RootId = rule.Id,
                Version = rule.Version,
                SnapshotJson = TaskPilotRuleMapping.Serialize(details, context.JsonOptions),
            }
        );
        ManagedSubscriptionRequest subscription = TaskPilotSystemSubscription.CreateRuleRequest(
            rule,
            request.SourceIds,
            request.EventTypes,
            details.Condition
        );
        rule.SubscriptionOutboxId = ExtensionOutbox.Enqueue(
            _dbContext,
            ExtensionOutboxKind.UpsertManagedSubscription,
            TaskPilotSystemSubscription.RuleIdempotencyKey(rule.Id, rule.Version),
            subscription,
            context.JsonOptions
        );
        rule.SubscriptionError = null;
        return JsonResponse(TaskPilotRuleMapping.ToDetails(rule, context.JsonOptions), context.JsonOptions);
    }

    private async Task<ExtensionEndpointResponse> UpdateRuleAsync(
        IExtensionContext context,
        TaskPilotRuleWriteRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Id is not { } id || request.Version is not { } version)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        TaskPilotRule rule =
            await _dbContext.Rules.SingleOrDefaultAsync(row => row.Id == id, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        EnsureVersion(rule.Version, version);
        if (rule.DeletedAt is not null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        return await SaveRuleAsync(context, request, rule, cancellationToken);
    }

    private async Task<ExtensionEndpointResponse> DeleteRuleAsync(
        IExtensionContext context,
        TaskPilotVersionRequest request,
        CancellationToken cancellationToken
    )
    {
        TaskPilotRule rule =
            await _dbContext.Rules.SingleOrDefaultAsync(row => row.Id == request.Id, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        EnsureVersion(rule.Version, request.Version);
        if (rule.DeletedAt is not null)
        {
            return JsonResponse(TaskPilotRuleMapping.ToDetails(rule, context.JsonOptions), context.JsonOptions);
        }

        rule.DeletedAt = _timeProvider.GetUtcNow();
        rule.Version++;
        rule.SubscriptionOutboxId = ExtensionOutbox.Enqueue(
            _dbContext,
            ExtensionOutboxKind.DeleteManagedSubscription,
            TaskPilotSystemSubscription.RuleIdempotencyKey(rule.Id, rule.Version),
            new TaskPilotDeleteManagedSubscriptionRequest
            {
                ManagedKey = $"rule:{rule.Id:D}",
                ManagedVersion = rule.Version,
            },
            context.JsonOptions
        );
        await _execution.CancelRuleRoundsAsync(_dbContext, context, rule, cancellationToken);
        return JsonResponse(TaskPilotRuleMapping.ToDetails(rule, context.JsonOptions), context.JsonOptions);
    }

    private async Task<ExtensionEndpointResponse> SetRulePausedAsync(
        IExtensionContext context,
        TaskPilotVersionRequest request,
        bool paused,
        CancellationToken cancellationToken
    )
    {
        TaskPilotRule rule =
            await _dbContext.Rules.SingleOrDefaultAsync(row => row.Id == request.Id, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        EnsureVersion(rule.Version, request.Version);
        if (rule.DeletedAt is not null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        if (rule.Paused != paused)
        {
            rule.Paused = paused;
        }

        if (!paused)
        {
            await _execution.StartRunnableAsync(_dbContext, context, cancellationToken);
        }

        return JsonResponse(TaskPilotRuleMapping.ToDetails(rule, context.JsonOptions), context.JsonOptions);
    }

    private async Task<ExtensionEndpointResponse> SetMatterStateAsync(
        IExtensionContext context,
        TaskPilotVersionRequest request,
        TaskPilotMatterState state,
        CancellationToken cancellationToken
    )
    {
        TaskPilotMatter matter =
            await _dbContext.Matters.SingleOrDefaultAsync(row => row.Id == request.Id, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        EnsureVersion(matter.Version, request.Version);
        matter.State = state;
        if (state == TaskPilotMatterState.Active)
        {
            matter.ConsecutiveFailures = 0;
            await _execution.StartRunnableAsync(_dbContext, context, cancellationToken);
        }

        matter.Version++;
        return JsonResponse(await ToMatterSummaryAsync(matter, cancellationToken), context.JsonOptions);
    }

    private async Task<ExtensionEndpointResponse> RunMatterAsync(
        IExtensionContext context,
        TaskPilotVersionRequest request,
        CancellationToken cancellationToken
    )
    {
        TaskPilotMatter matter =
            await _dbContext.Matters.SingleOrDefaultAsync(row => row.Id == request.Id, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        EnsureVersion(matter.Version, request.Version);
        if (matter.State is TaskPilotMatterState.Paused or TaskPilotMatterState.Stopped)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        TaskPilotRound? queued = await _dbContext
            .Rounds.Where(round => round.RootId == matter.Id && round.State == TaskPilotRoundState.Queued)
            .OrderBy(round => round.CreateTime)
            .FirstOrDefaultAsync(cancellationToken);
        if (queued is null)
        {
            TaskPilotRule rule = await _dbContext.Rules.SingleAsync(row => row.Id == matter.RootId, cancellationToken);
            TaskPilotRound? previous = await _dbContext
                .Rounds.Where(round => round.RootId == matter.Id)
                .OrderByDescending(round => round.CreateTime)
                .FirstOrDefaultAsync(cancellationToken);
            queued = new TaskPilotRound
            {
                RootId = matter.Id,
                RuleVersion = rule.Version,
                State = TaskPilotRoundState.Queued,
                Trigger = TaskPilotRoundTrigger.Manual,
                HeadEventId = previous?.HeadEventId,
                ChainId = previous?.ChainId ?? matter.ChainId,
                HeadHops = previous?.HeadHops ?? 0,
                ParentEventId = previous?.HeadEventId,
                Version = 0,
            };
            _dbContext.Rounds.Add(queued);
            _dbContext.RoundInputs.Add(
                new TaskPilotRoundInput
                {
                    RootId = queued.Id,
                    EventId = Guid.CreateVersion7(),
                    Kind = TaskPilotRoundInputKind.Manual,
                    PayloadJson = "{\"kind\":\"manual\"}",
                    ReceivedAt = _timeProvider.GetUtcNow(),
                    ChainId = queued.ChainId,
                    ParentEventId = queued.ParentEventId,
                    Hops = queued.HeadHops,
                }
            );
        }
        else
        {
            queued.NotBefore = null;
            queued.Version++;
        }

        matter.Version++;
        await _execution.StartRunnableAsync(_dbContext, context, cancellationToken);
        return JsonResponse(ToRoundSummary(queued), context.JsonOptions);
    }

    private async Task<ExtensionEndpointResponse> CancelMatterAsync(
        IExtensionContext context,
        TaskPilotMatterCancelRequest request,
        CancellationToken cancellationToken
    )
    {
        TaskPilotMatter matter =
            await _dbContext.Matters.SingleOrDefaultAsync(row => row.Id == request.Id, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        EnsureVersion(matter.Version, request.Version);
        TaskPilotRound? round = request.RoundId is { } roundId
            ? await _dbContext.Rounds.SingleOrDefaultAsync(
                row => row.Id == roundId && row.RootId == matter.Id,
                cancellationToken
            )
            : await _dbContext
                .Rounds.Where(row => row.RootId == matter.Id && IsActiveRound(row.State))
                .OrderByDescending(row => row.CreateTime)
                .FirstOrDefaultAsync(cancellationToken);
        if (round is null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        await _execution.CancelRoundAsync(_dbContext, context, matter, round, cancellationToken);
        matter.Version++;
        return JsonResponse(ToRoundSummary(round), context.JsonOptions);
    }

    private async Task<ExtensionEndpointResponse> CancelReviewAsync(
        IExtensionContext context,
        TaskPilotVersionRequest request,
        CancellationToken cancellationToken
    )
    {
        TaskPilotReview review =
            await _dbContext.Reviews.SingleOrDefaultAsync(row => row.Id == request.Id, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        EnsureVersion(review.Version, request.Version);
        await _execution.CancelReviewAsync(_dbContext, context, review, cancellationToken);
        return JsonResponse(ToReviewSummary(review), context.JsonOptions);
    }

    private async Task ValidateNoToolsTargetAsync(
        IExtensionContext context,
        TaskPilotRuleWriteRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Kind == TaskPilotRuleKind.Delivery)
        {
            return;
        }

        if (request.TargetId is not { } targetId)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        AgentRuntimeType runtimeType =
            request.TargetKind == AgentTargetKind.Agent ? AgentRuntimeType.Agent : AgentRuntimeType.Agentflow;
        bool supportsNoTools = await context
            .HostCapabilities.GetRequired<IAgentCatalogFacade>()
            .SupportsNoToolsAsync(runtimeType, targetId, cancellationToken);
        if (!supportsNoTools)
        {
            throw new AgwException(ErrorCodes.BackgroundToolPolicyUnsupported);
        }
    }

    private async Task<TaskPilotMatterSummary> ToMatterSummaryAsync(
        TaskPilotMatter matter,
        CancellationToken cancellationToken
    )
    {
        TaskPilotRound[] rounds = await _dbContext
            .Rounds.Where(round => round.RootId == matter.Id)
            .OrderByDescending(round => round.CreateTime)
            .ToArrayAsync(cancellationToken);
        return new TaskPilotMatterSummary
        {
            Id = matter.Id,
            RuleId = matter.RootId,
            MatterKey = matter.MatterKey,
            Title = matter.Title,
            Link = matter.Link,
            State = matter.State,
            ConversationId = matter.ConversationId,
            ConsecutiveFailures = matter.ConsecutiveFailures,
            Version = matter.Version,
            QueuedRounds = rounds.Count(round => round.State == TaskPilotRoundState.Queued),
            ActiveRoundId = rounds.FirstOrDefault(round => IsActiveRound(round.State))?.Id,
        };
    }

    private static TaskPilotRoundSummary ToRoundSummary(TaskPilotRound round) =>
        new()
        {
            Id = round.Id,
            MatterId = round.RootId,
            State = round.State,
            Trigger = round.Trigger,
            HeadEventId = round.HeadEventId,
            ConversationId = round.ConversationId,
            TurnId = round.TurnId,
            Attempt = round.Attempt,
            Outcome = round.Outcome,
            Summary = round.Summary,
            ReceiptError = round.ReceiptError,
            Error = round.Error,
            InputTokens = round.InputTokens,
            OutputTokens = round.OutputTokens,
            NotBefore = round.NotBefore,
            StartedAt = round.StartedAt,
            EndedAt = round.EndedAt,
            Version = round.Version,
        };

    private static TaskPilotRoundInputSummary ToRoundInputSummary(TaskPilotRoundInput input) =>
        new()
        {
            EventId = input.EventId,
            Kind = input.Kind,
            ReceivedAt = input.ReceivedAt,
            ChainId = input.ChainId,
            ParentEventId = input.ParentEventId,
            Hops = input.Hops,
            Summary = input.Summary,
            Link = input.Link,
            Event = TaskPilotRuleMapping.ParseOptionalJson(input.EventJson),
            Payload = TaskPilotRuleMapping.ParseOptionalJson(input.PayloadJson),
        };

    private static TaskPilotRoundAttemptSummary ToRoundAttemptSummary(TaskPilotRoundAttempt attempt) =>
        new()
        {
            Attempt = attempt.Attempt,
            TurnId = attempt.TurnId,
            Purpose = attempt.Purpose,
            State = attempt.State,
            Error = attempt.Error,
            InputTokens = attempt.InputTokens,
            OutputTokens = attempt.OutputTokens,
            StartedAt = attempt.StartedAt,
            EndedAt = attempt.EndedAt,
        };

    private static TaskPilotCardSummary ToCardSummary(TaskPilotCard card) =>
        new()
        {
            Id = card.Id,
            RoundId = card.RoundId,
            Kind = card.Kind,
            CardKey = card.CardKey,
            Title = card.Title,
            Text = card.Text,
            Options =
                JsonSerializer.Deserialize<Agw.Notifications.Contracts.NotificationOption[]>(
                    card.OptionsJson,
                    TaskPilotJsonContext.Default.Options
                ) ?? [],
            AllowText = card.AllowText,
            Recommend = card.Recommend,
            About = card.About,
            State = card.State,
            NotificationId = card.NotificationId,
            ConversationId = card.ConversationId,
            Answer = TaskPilotRuleMapping.ParseOptionalJson(card.AnswerJson),
            ResolvedReason = card.ResolvedReason,
        };

    private static TaskPilotReviewSummary ToReviewSummary(TaskPilotReview review) =>
        new()
        {
            Id = review.Id,
            MatterId = review.RootId,
            ReviewKey = review.ReviewKey,
            DueAt = review.DueAt,
            About = review.About,
            State = review.State,
            RequestSeq = review.RequestSeq,
            Version = review.Version,
        };

    private static TaskPilotDeliverySummary ToDeliverySummary(TaskPilotDelivery delivery) =>
        new()
        {
            Id = delivery.Id,
            MatterId = delivery.RootId,
            Repository = delivery.Repository,
            PullRequestNumber = delivery.PullRequestNumber,
            Mode = delivery.Mode,
            Stage = delivery.Stage,
            HeadSha = delivery.HeadSha,
            ReviewedSha = delivery.ReviewedSha,
            MergeSha = delivery.MergeSha,
            LastError = delivery.LastError,
            Version = delivery.Version,
        };

    private static TRequest ReadBody<TRequest>(ExtensionEndpointRequest request, IExtensionContext context)
        where TRequest : class
    {
        if (request.Body is not { ValueKind: JsonValueKind.Object } body)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        try
        {
            return JsonSerializer.Deserialize<TRequest>(body, context.JsonOptions)
                ?? throw new AgwException(ErrorCodes.InvalidParam);
        }
        catch (JsonException exception)
        {
            throw new AgwException(ErrorCodes.InvalidParam, exception.Message);
        }
    }

    private static Guid RequiredQueryGuid(ExtensionEndpointRequest request, string key) =>
        OptionalQueryGuid(request, key) ?? throw new AgwException(ErrorCodes.InvalidParam);

    private static Guid? OptionalQueryGuid(ExtensionEndpointRequest request, string key)
    {
        if (!request.Query.TryGetValue(key, out IReadOnlyList<string>? values) || values.Count == 0)
        {
            return null;
        }

        return Guid.TryParse(values[0], out Guid id) ? id : throw new AgwException(ErrorCodes.InvalidParam);
    }

    private static bool IsActiveRound(TaskPilotRoundState state) =>
        state
            is TaskPilotRoundState.Submitting
                or TaskPilotRoundState.Running
                or TaskPilotRoundState.ReceiptRepair
                or TaskPilotRoundState.Cancelling;

    private static void EnsureVersion(long current, long expected)
    {
        if (current != expected)
        {
            throw new AgwException(ErrorCodes.EventSubscriptionModificationConflict);
        }
    }

    private static void ValidateRule(TaskPilotRuleWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (
            string.IsNullOrWhiteSpace(request.Name)
            || request.ProjectId == Guid.Empty
            || request.EventTypes.Count == 0 && request.Kind == TaskPilotRuleKind.Agent
            || request.MaxConcurrency is < 1 or > 64
            || request.DataScope.MaximumBytes is < 1 or > 1_000_000
            || request.EventTypes.Any(string.IsNullOrWhiteSpace)
            || request.EmitTypes.Any(eventType =>
                string.IsNullOrWhiteSpace(eventType)
                || eventType.StartsWith("agw.", StringComparison.Ordinal)
                || !eventType.StartsWith("taskpilot.", StringComparison.Ordinal)
                || eventType.Contains('*', StringComparison.Ordinal)
            )
            || request.Retry.DelayMinutes.Count == 0
            || request.Retry.DelayMinutes.Any(delay => delay <= 0)
            || request.Retry.MaximumDelayMinutes is < 1 or > 60
            || request.Retry.NotificationThreshold < 1
            || request.Retry.StopThreshold < request.Retry.NotificationThreshold
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        if (request.Kind == TaskPilotRuleKind.Agent && request.TargetId is null)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        if (request.Condition is { } condition)
        {
            JsonLogic.Apply(JsonNode.Parse(condition.GetRawText()), new JsonObject());
        }

        if (request.MatterKey is { } matterKey)
        {
            JsonLogic.Apply(JsonNode.Parse(matterKey.GetRawText()), new JsonObject());
        }

        foreach (var item in request.DataScope.EventPaths)
        {
            if (!request.EventTypes.Contains(item.Key, StringComparer.Ordinal))
            {
                throw new AgwException(ErrorCodes.InvalidParam);
            }

            foreach (string path in item.Value)
            {
                if (!JsonPath.TryParse(path, out _))
                {
                    throw new AgwException(ErrorCodes.InvalidParam);
                }
            }
        }
    }

    private static ExtensionEndpointResponse JsonResponse<T>(T value, JsonSerializerOptions jsonOptions) =>
        ExtensionEndpointResponse.FromJson(JsonSerializer.SerializeToElement(value, jsonOptions));
}
