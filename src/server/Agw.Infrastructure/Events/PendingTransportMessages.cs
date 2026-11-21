using Agw.Events.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Events.Observability;
using Agw.Shared.Contracts.Coordination;
using Microsoft.Extensions.Logging;

namespace Agw.Infrastructure.Events;

public sealed class PendingTransportMessages : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<(string Topic, Guid EntityId), OutboxMessage> _messages = [];
    private readonly Dictionary<string, IApplicationLockLease> _leases = new(StringComparer.Ordinal);

    public bool HasMessages => _messages.Count > 0;

    public void ValidateLeases()
    {
        foreach (var lease in _leases.Values)
            lease.HandleLostToken.ThrowIfCancellationRequested();
    }

    public void Stage(OutboxMessage message) => _messages[(message.Topic, message.EntityId)] = message;

    public async Task AcquireAsync(
        IApplicationLock applicationLock,
        string resource,
        CancellationToken cancellationToken
    )
    {
        if (_leases.ContainsKey(resource))
            return;
        _leases.Add(resource, await applicationLock.AcquireAsync(resource, cancellationToken));
    }

    public async Task FlushAsync(IEventTransport transport, ILogger logger)
    {
        var messages = _messages.Values.ToArray();
        _messages.Clear();
        foreach (var message in messages)
        {
            try
            {
                await transport.PublishAsync(message.Topic, message.Message, CancellationToken.None);
            }
            catch (Exception exception)
            {
                EventTelemetry.PublishFailures.Add(1, new KeyValuePair<string, object?>("topic", message.Topic));
                logger.LogError(
                    exception,
                    "Event transport publish failed after commit for {Topic} record {EntityId}.",
                    message.Topic,
                    message.EntityId
                );
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _messages.Clear();
        var leases = _leases.Values.ToArray();
        _leases.Clear();
        foreach (var lease in leases)
            await lease.DisposeAsync();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
