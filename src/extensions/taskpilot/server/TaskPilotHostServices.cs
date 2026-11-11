using System.Text.Json;
using Agw.Events.Contracts;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Extensions.TaskPilot;

internal static class TaskPilotCaller
{
    public static string MutationLeaseName(string userId) => $"user:{userId}:taskpilot-mutations";

    public static ExtensionCaller Require(IExtensionContext context)
    {
        ExtensionCaller? caller = context.HostCapabilities.GetRequired<IExtensionCallerAccessor>().Current;
        if (
            caller is null
            || caller.ExtensionId != context.ExtensionId
            || caller.GenerationId != context.GenerationId
            || string.IsNullOrWhiteSpace(caller.UserId)
        )
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }

        return caller;
    }
}

internal static class TaskPilotSystemSubscription
{
    public const string ExecutionCompleted = "agw.execution.completed";
    public const string NotificationAnswered = "agw.notification.answered";
    public const string ReviewDue = "taskpilot.review.due";
    public const string RoundRetry = "taskpilot.round.retry";
    public const string DeliveryTick = "taskpilot.delivery.tick";

    private static readonly string[] EventTypes =
    [
        ExecutionCompleted,
        NotificationAnswered,
        ReviewDue,
        RoundRetry,
        DeliveryTick,
    ];

    public static ManagedSubscriptionRequest CreateRequest(IExtensionContext context, long version)
    {
        Guid extensionId = context.ExtensionId;
        using JsonDocument filter = JsonDocument.Parse(
            $$"""
            {
              "or": [
                {"and": [
                  {"===": [{"var": "type"}, "{{ExecutionCompleted}}"]},
                  {"===": [{"var": "data.caller"}, "ext:{{extensionId:D}}"]}
                ]},
                {"and": [
                  {"===": [{"var": "type"}, "{{NotificationAnswered}}"]},
                  {"===": [{"var": "data.origin"}, "{{extensionId:D}}"]}
                ]},
                {"in": [{"var": "type"}, ["{{ReviewDue}}", "{{RoundRetry}}", "{{DeliveryTick}}"]]}
              ]
            }
            """
        );

        return new ManagedSubscriptionRequest
        {
            ManagedKey = "system",
            ManagedVersion = version,
            Name = "TaskPilot system events",
            PublisherKeys = ["agw.execution", "agw.notifications", $"agw.scheduler:ext:{extensionId:D}"],
            EventTypes = EventTypes,
            Filter = filter.RootElement.Clone(),
            HandlerId = TaskPilotEventHandlers.SystemEvents,
            HandlerConfig = JsonDocument.Parse("{}").RootElement.Clone(),
        };
    }

    public static async Task EnsureAsync(
        TaskPilotDbContext dbContext,
        IExtensionContext context,
        CancellationToken cancellationToken
    )
    {
        TaskPilotUserState? state = await dbContext.UserStates.SingleOrDefaultAsync(cancellationToken);
        if (
            state is not null
            && (
                state.SystemSubscriptionOutboxId is not null
                || state.SystemSubscriptionId is not null && state.SystemSubscriptionError is null
            )
        )
        {
            return;
        }

        if (state is null)
        {
            state = new TaskPilotUserState { SystemSubscriptionVersion = 1 };
            dbContext.UserStates.Add(state);
        }
        else
        {
            state.SystemSubscriptionVersion++;
        }

        ManagedSubscriptionRequest request = CreateRequest(context, state.SystemSubscriptionVersion);
        state.SystemSubscriptionOutboxId = ExtensionOutbox.Enqueue(
            dbContext,
            ExtensionOutboxKind.UpsertManagedSubscription,
            IdempotencyKey(state.SystemSubscriptionVersion),
            request,
            context.JsonOptions
        );
        state.SystemSubscriptionError = null;
    }

    public static string IdempotencyKey(long version) => $"taskpilot:system:subscription:{version}";

    public static string RuleIdempotencyKey(Guid ruleId, long version) => $"rule:{ruleId:D}:subscription:{version}";

    public static ManagedSubscriptionRequest CreateRuleRequest(
        TaskPilotRule rule,
        IReadOnlyList<Guid> sourceIds,
        IReadOnlyList<string> eventTypes,
        JsonElement? filter
    )
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new ManagedSubscriptionRequest
        {
            ManagedKey = $"rule:{rule.Id:D}",
            ManagedVersion = rule.Version,
            Name = rule.Name,
            SourceIds = sourceIds,
            EventTypes = eventTypes,
            Filter = filter?.Clone(),
            HandlerId = TaskPilotEventHandlers.RuleEvents,
            HandlerConfig = JsonSerializer.SerializeToElement(
                new TaskPilotRuleHandlerConfig { RuleId = rule.Id },
                TaskPilotJsonContext.Default.TaskPilotRuleHandlerConfig
            ),
        };
    }
}
