namespace Agw.Agents.Execution.Turns;

public interface IRuntimeTurnContextAccessor
{
    RuntimeTurnContext? Current { get; }
}

public sealed class RuntimeTurnContextAccessor : IRuntimeTurnContextAccessor
{
    private readonly AsyncLocal<RuntimeTurnContext?> _current = new();

    public RuntimeTurnContext? Current => _current.Value;

    internal IDisposable Push(RuntimeTurnContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = _current.Value;
        _current.Value = context;
        return new Scope(this, previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly RuntimeTurnContextAccessor _accessor;
        private readonly RuntimeTurnContext? _previous;
        private int _disposed;

        public Scope(RuntimeTurnContextAccessor accessor, RuntimeTurnContext? previous)
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
