using Agw.Agents.Execution.HumanInteraction.Application;

namespace Agw.Agents.Execution.HumanInteraction;

public sealed class HumanInteractionContextAccessor : IHumanInteractionContextAccessor
{
    private readonly AsyncLocal<IHumanInteractionChannel?> _current = new();
    private readonly AsyncLocal<IInteractionRequestRegistry?> _requests = new();
    private readonly AsyncLocal<InteractionPermissionState?> _permissions = new();
    private readonly AsyncLocal<Func<CancellationToken, ValueTask>?> _refreshPermissions = new();

    public IHumanInteractionChannel? Current => _current.Value;
    public IInteractionRequestRegistry? Requests => _requests.Value;
    internal InteractionPermissionState? PermissionState => _permissions.Value;

    internal ValueTask RefreshPermissionsAsync(CancellationToken cancellationToken = default) =>
        _refreshPermissions.Value?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;

    internal IDisposable Push(
        IHumanInteractionChannel? channel,
        IInteractionRequestRegistry? requests = null,
        InteractionPermissionState? permissions = null,
        Func<CancellationToken, ValueTask>? refreshPermissions = null
    )
    {
        var previous = _current.Value;
        var previousRequests = _requests.Value;
        var previousPermissions = _permissions.Value;
        var previousRefresh = _refreshPermissions.Value;
        _current.Value = channel;
        _requests.Value = requests;
        _permissions.Value = permissions;
        _refreshPermissions.Value =
            refreshPermissions ?? (ReferenceEquals(previousPermissions, permissions) ? previousRefresh : null);
        return new Scope(this, previous, previousRequests, previousPermissions, previousRefresh);
    }

    internal IDisposable Suppress() => Push(null);

    private sealed class Scope : IDisposable
    {
        private readonly HumanInteractionContextAccessor _accessor;
        private readonly IHumanInteractionChannel? _previous;
        private readonly IInteractionRequestRegistry? _previousRequests;
        private readonly InteractionPermissionState? _previousPermissions;
        private readonly Func<CancellationToken, ValueTask>? _previousRefresh;
        private int _disposed;

        public Scope(
            HumanInteractionContextAccessor accessor,
            IHumanInteractionChannel? previous,
            IInteractionRequestRegistry? previousRequests,
            InteractionPermissionState? previousPermissions,
            Func<CancellationToken, ValueTask>? previousRefresh
        )
        {
            _accessor = accessor;
            _previous = previous;
            _previousRequests = previousRequests;
            _previousPermissions = previousPermissions;
            _previousRefresh = previousRefresh;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _accessor._current.Value = _previous;
                _accessor._requests.Value = _previousRequests;
                _accessor._permissions.Value = _previousPermissions;
                _accessor._refreshPermissions.Value = _previousRefresh;
            }
        }
    }
}
