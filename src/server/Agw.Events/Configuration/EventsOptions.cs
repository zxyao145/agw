namespace Agw.Events.Configuration;

public sealed class EventsOptions
{
    public const string SectionName = "Events";

    public EventTransportOptions Transport { get; set; } = new();

    public EventOutboxOptions Outbox { get; set; } = new();

    public EventDispatchOptions Dispatch { get; set; } = new();

    public EventPayloadOptions Payload { get; set; } = new();

    public EventDeliveryOptions Delivery { get; set; } = new();

    public EventHttpPollOptions HttpPoll { get; set; } = new();
}

public sealed class EventTransportOptions
{
    public string Provider { get; set; } = "InProcess";

    public EventRedisOptions Redis { get; set; } = new();
}

public sealed class EventRedisOptions
{
    public string? ConnectionString { get; set; }

    public int StreamMaxLength { get; set; } = 100000;
}

public sealed class EventOutboxOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class EventDispatchOptions
{
    public int MaxHops { get; set; } = 8;
}

public sealed class EventPayloadOptions
{
    public int MaxBytes { get; set; } = 262144;
}

public sealed class EventDeliveryOptions
{
    public int MaxAttempts { get; set; } = 10;

    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan RunningTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan GetRetryDelay(int attempt, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } requested)
        {
            return requested > MaxBackoff ? MaxBackoff : requested;
        }

        var delay = BaseBackoff;
        for (var index = 1; index < attempt && delay < MaxBackoff; index++)
        {
            delay = delay.Ticks > MaxBackoff.Ticks / 2 ? MaxBackoff : TimeSpan.FromTicks(delay.Ticks * 2);
        }
        return delay;
    }
}

public sealed class EventHttpPollOptions
{
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromMinutes(1);

    public int MaxPages { get; set; } = 20;

    public int MaxItems { get; set; } = 5000;

    public int MaxResponseBytes { get; set; } = 8 * 1024 * 1024;

    public bool AllowPrivateNetworks { get; set; }
}
