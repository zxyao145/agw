namespace Agw.Shared.Data.Entities.Events;

public enum EventDispatchState
{
    Pending,
    Dispatched,
    NoMatch,
    HopLimitExceeded,
}

public enum EventSubscriptionTargetKind
{
    Agent,
    Agentflow,
    ExtensionHandler,
}

public enum EventSubscriptionManagedState
{
    Active,
    Deleted,
}

public enum EventDeliveryState
{
    Pending,
    Running,
    WaitingExecution,
    Succeeded,
    RetryScheduled,
    Held,
    Parked,
    Cancelled,
}

public enum EventDeliveryAttemptResult
{
    Succeeded,
    Retry,
    Failed,
    Held,
}

public enum EventScheduleKind
{
    Once,
    Interval,
    Cron,
}

public enum EventScheduleState
{
    Active,
    Completed,
    Cancelled,
}

public enum EventOutboxState
{
    Pending,
    Published,
    Completed,
}
