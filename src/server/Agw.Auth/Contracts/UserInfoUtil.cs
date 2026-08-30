using System.Security.Claims;
using Agw.Shared.Exceptions;

namespace Agw.Auth.Contracts;

public static class UserInfoUtil
{
    private static readonly AsyncLocal<ClaimsPrincipal?> CurrentUser = new();
    private static readonly AsyncLocal<bool> UserContextActive = new();
    private static readonly AsyncLocal<int> SystemScopeDepth = new();

    public static ClaimsPrincipal? Current
    {
        get => CurrentUser.Value;
        set
        {
            CurrentUser.Value = value;
            UserContextActive.Value = value != null;
        }
    }

    /// <summary>
    /// Indicates that the current asynchronous flow has established an HTTP or
    /// execution user context.
    /// </summary>
    public static bool IsContextActive => UserContextActive.Value;

    /// <summary>
    /// Indicates that a trusted infrastructure operation is intentionally
    /// scanning all owners. This scope is reserved for startup seeding and
    /// background schedulers; interactive application code must never use it.
    /// </summary>
    public static bool IsSystemScopeActive => SystemScopeDepth.Value > 0;

    public static string? UserId
    {
        get
        {
            var userId = Current?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            return string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
        }
    }

    public static bool IsAuthenticated => Current?.Identity?.IsAuthenticated == true;

    public static string RequiredUserId
    {
        get
        {
            if (!IsAuthenticated)
                throw new AgwException(ErrorCodes.AuthenticationRequired);
            return string.IsNullOrWhiteSpace(UserId)
                ? throw new AgwException(ErrorCodes.AuthenticationRequired, "A stable user id is required.")
                : UserId;
        }
    }

    /// <summary>
    /// Establishes an isolated user context for the current asynchronous flow.
    /// </summary>
    public static IDisposable Push(ClaimsPrincipal? principal)
    {
        var previousPrincipal = Current;
        var previousActive = IsContextActive;
        Current = principal;
        UserContextActive.Value = true;
        return new UserContextScope(previousPrincipal, previousActive);
    }

    /// <summary>
    /// Establishes a restricted system scope for infrastructure maintenance
    /// paths that must scan records across owners.
    /// </summary>
    public static IDisposable PushSystemScope()
    {
        SystemScopeDepth.Value++;
        return new SystemScope();
    }

    private sealed class UserContextScope : IDisposable
    {
        private readonly ClaimsPrincipal? _previousPrincipal;
        private readonly bool _previousActive;
        private int _disposed;

        public UserContextScope(ClaimsPrincipal? previousPrincipal, bool previousActive)
        {
            _previousPrincipal = previousPrincipal;
            _previousActive = previousActive;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Current = _previousPrincipal;
            UserContextActive.Value = _previousActive;
        }
    }

    private sealed class SystemScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            SystemScopeDepth.Value = Math.Max(0, SystemScopeDepth.Value - 1);
        }
    }
}
