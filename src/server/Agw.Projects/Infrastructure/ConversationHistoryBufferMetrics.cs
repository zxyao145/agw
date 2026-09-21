using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Agw.Projects.Infrastructure;

internal static class ConversationHistoryBufferMetrics
{
    private static readonly ConcurrentDictionary<Guid, PendingBuffer> Pending = new();
    private static readonly Meter Meter = new("Agw.ConversationHistory.Persistence");
    private static readonly ObservableGauge<long> Bytes = Meter.CreateObservableGauge(
        "agw.history.pending.bytes",
        () => Pending.Values.Sum(buffer => buffer.Bytes),
        "By"
    );
    private static readonly ObservableGauge<double> Age = Meter.CreateObservableGauge(
        "agw.history.pending.oldest_age",
        () =>
            Pending
                .Values.Select(buffer => Math.Max(0, (buffer.Clock.GetUtcNow() - buffer.Since).TotalSeconds))
                .DefaultIfEmpty(0)
                .Max(),
        "s"
    );

    internal static void Update(Guid id, long bytes, DateTimeOffset since, TimeProvider clock)
    {
        if (bytes == 0)
            Pending.TryRemove(id, out _);
        else
            Pending[id] = new PendingBuffer(bytes, since, clock);
    }

    private sealed record PendingBuffer(long Bytes, DateTimeOffset Since, TimeProvider Clock);
}
