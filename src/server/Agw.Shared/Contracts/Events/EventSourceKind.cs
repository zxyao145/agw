namespace Agw.Shared.Contracts.Events;

public enum EventSourceKind
{
    Timer,
    Webhook,
    HttpPoll,
    Provider,
    Publisher,
}
