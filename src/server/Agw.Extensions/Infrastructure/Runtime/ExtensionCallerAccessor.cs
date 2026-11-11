using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Extensions.Infrastructure.Runtime;

internal interface IExtensionCallerScope
{
    IDisposable Push(ExtensionCaller caller);
}

internal sealed class ExtensionCallerAccessor : IExtensionCallerAccessor, IExtensionCallerScope
{
    private readonly AsyncLocal<ExtensionCaller?> _current = new();

    public ExtensionCaller? Current => _current.Value;

    public IDisposable Push(ExtensionCaller caller)
    {
        if (caller.ExtensionId == Guid.Empty || caller.GenerationId == Guid.Empty || caller.InvocationId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }

        ExtensionCaller? previous = _current.Value;
        _current.Value = caller;
        return new Restore(this, previous);
    }

    private sealed class Restore(ExtensionCallerAccessor owner, ExtensionCaller? previous) : IDisposable
    {
        private ExtensionCallerAccessor? _owner = owner;

        public void Dispose()
        {
            ExtensionCallerAccessor? current = Interlocked.Exchange(ref _owner, null);
            if (current is not null)
            {
                current._current.Value = previous;
            }
        }
    }
}
