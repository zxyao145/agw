namespace Agw.Agents.Execution.Turns;

public sealed class HumanInteractionContextAccessor : IHumanInteractionContextAccessor
{
    private readonly AsyncLocal<IHumanInteractionChannel?> _current = new();

    public IHumanInteractionChannel? Current => _current.Value;

    internal IDisposable Push(IHumanInteractionChannel? channel)
    {
        var previous = _current.Value;
        _current.Value = channel;
        return new Scope(this, previous);
    }

    internal IDisposable Suppress() => Push(null);

    private sealed class Scope : IDisposable
    {
        private readonly HumanInteractionContextAccessor _accessor;
        private readonly IHumanInteractionChannel? _previous;
        private int _disposed;

        public Scope(HumanInteractionContextAccessor accessor, IHumanInteractionChannel? previous)
        {
            _accessor = accessor;
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _accessor._current.Value = _previous;
            }
        }
    }
}
