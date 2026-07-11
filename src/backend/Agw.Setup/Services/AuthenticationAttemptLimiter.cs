using System.Collections.Concurrent;

using Microsoft.AspNetCore.Http;

namespace Agw.Setup.Services;

public sealed class AuthenticationAttemptLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const int FailureLimit = 5;

    private readonly ConcurrentDictionary<string, FailureWindow> _windows = new(StringComparer.Ordinal);

    public bool IsBlocked(string clientKey, DateTimeOffset now)
    {
        if (!_windows.TryGetValue(clientKey, out var window)) return false;
        lock (window.Failures)
        {
            RemoveExpired(window.Failures, now);
            return window.Failures.Count >= FailureLimit;
        }
    }

    public void RecordFailure(string clientKey, DateTimeOffset now)
    {
        var window = _windows.GetOrAdd(clientKey, _ => new FailureWindow());
        lock (window.Failures)
        {
            RemoveExpired(window.Failures, now);
            window.Failures.Enqueue(now);
        }
    }

    public static string GetClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static void RemoveExpired(Queue<DateTimeOffset> failures, DateTimeOffset now)
    {
        var cutoff = now - Window;
        while (failures.TryPeek(out var failure) && failure < cutoff) failures.Dequeue();
    }

    private sealed class FailureWindow
    {
        public Queue<DateTimeOffset> Failures { get; } = new();
    }
}
