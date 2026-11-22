namespace Agw.Events.Domain.ValueObjects;

public readonly record struct EventTimerInterval
{
    public required string Text { get; init; }

    public required TimeSpan Duration { get; init; }
}
