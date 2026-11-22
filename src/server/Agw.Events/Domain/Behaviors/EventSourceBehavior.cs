using Agw.Events.Contracts;
using Agw.Events.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;

namespace Agw.Events.Domain.Behaviors;

public sealed class EventSourceBehavior
{
    private readonly EventSource _source;

    public EventSourceBehavior(EventSource source)
    {
        _source = source;
    }

    public void ConfigureJobTimer(string configJson, EventTimerInterval? requestedInterval, string? previousInterval)
    {
        if (requestedInterval is { } interval && interval.Text != previousInterval)
        {
            const int stepSeconds = EventJobSchedule.IntervalStepSeconds;
            const long stepTicks = stepSeconds * TimeSpan.TicksPerSecond;
            var ticks = interval.Duration.Ticks;
            if (ticks < stepTicks || ticks % stepTicks != 0)
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Job interval must be at least {stepSeconds} seconds and a multiple of {stepSeconds} seconds."
                );
            }
        }
        _source.ConfigJson = configJson;
    }
}
