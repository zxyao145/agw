using System.Text.Json;
using Agw.Events.Contracts;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Shared.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agw.Extensions.TaskPilot;

internal abstract class TaskPilotEventHandler : ExtensionEventHandlerBase<TaskPilotDbContext>
{
    private const int MaxPersistenceAttempts = 5;
    private const int RetryDelayStepMilliseconds = 20;
    private readonly TaskPilotDbContext _dbContext;

    protected TaskPilotEventHandler(TaskPilotDbContext dbContext, TaskPilotEventProcessor processor)
    {
        _dbContext = dbContext;
        Processor = processor;
    }

    protected TaskPilotEventProcessor Processor { get; }

    public async ValueTask<ExtensionEventHandlerResult> ProcessAsync(
        IExtensionContext context,
        ExtensionEventDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        ExtensionCaller caller = TaskPilotCaller.Require(context);
        await using IExtensionLease lease = await context.Leases.AcquireAsync(
            TaskPilotCaller.MutationLeaseName(caller.UserId!),
            cancellationToken
        );
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lease.HandleLostToken
        );
        ExtensionEventHandlerResult result = ExtensionEventHandlerResult.Retry(
            TimeSpan.FromSeconds(1),
            "TaskPilot event persistence was contended."
        );
        for (int attempt = 1; attempt <= MaxPersistenceAttempts; attempt++)
        {
            try
            {
                result = await base.ProcessAsync(_dbContext, context, delivery, linkedCancellation.Token);
                break;
            }
            catch (DbUpdateException exception) when (TaskPilotEventConflictRetry.IsRetryable(exception))
            {
                _dbContext.ChangeTracker.Clear();
                context.Logger.LogDebug(
                    exception,
                    "TaskPilot delivery {DeliveryId} conflicted during persistence attempt {Attempt}.",
                    delivery.DeliveryId,
                    attempt
                );
                if (attempt == MaxPersistenceAttempts)
                {
                    break;
                }
                await Task.Delay(
                    TimeSpan.FromMilliseconds(RetryDelayStepMilliseconds * attempt),
                    linkedCancellation.Token
                );
            }
        }
        lease.HandleLostToken.ThrowIfCancellationRequested();
        return result;
    }
}

internal sealed class TaskPilotRuleEventHandler : TaskPilotEventHandler
{
    public TaskPilotRuleEventHandler(TaskPilotDbContext dbContext, TaskPilotEventProcessor processor)
        : base(dbContext, processor) { }

    protected override ValueTask<ExtensionEventHandlerResult> HandleEventAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        ExtensionEventDelivery delivery,
        CancellationToken cancellationToken
    ) => Processor.HandleRuleEventAsync(dbContext, context, delivery, cancellationToken);
}

internal sealed class TaskPilotSystemEventHandler : TaskPilotEventHandler
{
    public TaskPilotSystemEventHandler(TaskPilotDbContext dbContext, TaskPilotEventProcessor processor)
        : base(dbContext, processor) { }

    protected override ValueTask<ExtensionEventHandlerResult> HandleEventAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        ExtensionEventDelivery delivery,
        CancellationToken cancellationToken
    ) => Processor.HandleSystemEventAsync(dbContext, context, delivery, cancellationToken);
}

internal sealed class TaskPilotEventProcessor
{
    private readonly TaskPilotExecutionCoordinator _execution;
    private readonly TaskPilotDeliveryCoordinator _delivery;

    public TaskPilotEventProcessor(TaskPilotExecutionCoordinator execution, TaskPilotDeliveryCoordinator delivery)
    {
        _execution = execution;
        _delivery = delivery;
    }

    public async ValueTask<ExtensionEventHandlerResult> HandleRuleEventAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        ExtensionEventDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        await TaskPilotSystemSubscription.EnsureAsync(dbContext, context, cancellationToken);
        if (
            delivery.HandlerConfig is not { ValueKind: JsonValueKind.Object } config
            || !config.TryGetProperty("ruleId", out JsonElement ruleIdElement)
            || !ruleIdElement.TryGetGuid(out Guid ruleId)
        )
        {
            return ExtensionEventHandlerResult.Failed("The managed TaskPilot rule subscription is invalid.");
        }

        TaskPilotRule? rule = await dbContext.Rules.SingleOrDefaultAsync(row => row.Id == ruleId, cancellationToken);
        if (rule is null || rule.DeletedAt is not null || !rule.Enabled)
        {
            return ExtensionEventHandlerResult.Succeeded;
        }

        if (rule.Kind == TaskPilotRuleKind.Delivery)
        {
            return await _delivery.HandleRuleEventAsync(dbContext, context, rule, delivery, cancellationToken);
        }

        return await _execution.EnqueueEventAsync(dbContext, context, rule, delivery, cancellationToken);
    }

    public async ValueTask<ExtensionEventHandlerResult> HandleSystemEventAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        ExtensionEventDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        var publisherKey = delivery.Event.Type switch
        {
            TaskPilotSystemSubscription.ExecutionCompleted => "agw.execution",
            TaskPilotSystemSubscription.NotificationAnswered => "agw.notifications",
            TaskPilotSystemSubscription.DeliveryTick
            or TaskPilotSystemSubscription.ReviewDue
            or TaskPilotSystemSubscription.RoundRetry => $"agw.scheduler:ext:{context.ExtensionId:D}",
            _ => null,
        };
        if (publisherKey is null)
            return ExtensionEventHandlerResult.Succeeded;

        var source = await context
            .HostCapabilities.GetRequired<IEventSourceFacade>()
            .GetAsync(delivery.SourceId, cancellationToken);
        if (source is not { Kind: EventSourceKind.Publisher } || source.PublisherKey != publisherKey)
        {
            context.Logger.LogError(
                "TaskPilot delivery {DeliveryId} for {EventType} has an untrusted source {SourceId}",
                delivery.DeliveryId,
                delivery.Event.Type,
                delivery.SourceId
            );
            return ExtensionEventHandlerResult.Succeeded;
        }

        await TaskPilotSystemSubscription.EnsureAsync(dbContext, context, cancellationToken);
        switch (delivery.Event.Type)
        {
            case TaskPilotSystemSubscription.ExecutionCompleted:
                if (await _delivery.HandleExecutionCompletedAsync(dbContext, context, delivery, cancellationToken))
                {
                    return ExtensionEventHandlerResult.Succeeded;
                }

                return await _execution.HandleExecutionCompletedAsync(dbContext, context, delivery, cancellationToken);
            case TaskPilotSystemSubscription.NotificationAnswered:
                if (await _delivery.HandleAnswerAsync(dbContext, context, delivery, cancellationToken))
                {
                    return ExtensionEventHandlerResult.Succeeded;
                }

                return await _execution.HandleNotificationAnsweredAsync(
                    dbContext,
                    context,
                    delivery,
                    cancellationToken
                );
            case TaskPilotSystemSubscription.DeliveryTick:
                return await _delivery.HandleTickAsync(dbContext, context, delivery, cancellationToken);
            case TaskPilotSystemSubscription.ReviewDue:
                return await _execution.HandleReviewDueAsync(dbContext, context, delivery, cancellationToken);
            case TaskPilotSystemSubscription.RoundRetry:
                return await _execution.HandleRetryDueAsync(dbContext, context, delivery, cancellationToken);
            default:
                return ExtensionEventHandlerResult.Succeeded;
        }
    }
}
