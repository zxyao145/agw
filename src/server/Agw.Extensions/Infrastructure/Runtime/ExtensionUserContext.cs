using Agw.Extensions.Sdk.Database;

namespace Agw.Extensions.Infrastructure.Runtime;

internal sealed class ExtensionUserContext : IExtensionUserContext
{
    private readonly AsyncLocal<string?> _currentUserId = new();
    private readonly AsyncLocal<Guid?> _currentInvocationId = new();

    public string? UserId => _currentUserId.Value;

    public Guid? InvocationId => _currentInvocationId.Value;

    public IDisposable Push(string? userId, Guid? invocationId = null)
    {
        string? previous = _currentUserId.Value;
        Guid? previousInvocationId = _currentInvocationId.Value;
        _currentUserId.Value = userId;
        _currentInvocationId.Value = invocationId;
        return new Restore(this, previous, previousInvocationId);
    }

    public void Clear()
    {
        _currentUserId.Value = null;
        _currentInvocationId.Value = null;
    }

    private sealed class Restore : IDisposable
    {
        private ExtensionUserContext? _context;
        private readonly string? _userId;
        private readonly Guid? _invocationId;

        public Restore(ExtensionUserContext context, string? userId, Guid? invocationId)
        {
            _context = context;
            _userId = userId;
            _invocationId = invocationId;
        }

        public void Dispose()
        {
            ExtensionUserContext? current = Interlocked.Exchange(ref _context, null);
            if (current is not null)
            {
                current._currentUserId.Value = _userId;
                current._currentInvocationId.Value = _invocationId;
            }
        }
    }
}
