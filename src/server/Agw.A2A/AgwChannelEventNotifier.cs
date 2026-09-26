using System.Threading.Channels;
using A2A;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;

namespace Agw.A2A;

public sealed class AgwChannelEventNotifier
{
    private const int SubscriberCapacity = 128;
    private const int MaximumSubscribers = 64;
    private readonly Dictionary<string, HashSet<Channel<StreamResponse>>> _subscribers = new(StringComparer.Ordinal);
    private readonly Lock _subscriberLock = new();
    private readonly InMemoryApplicationLock _taskLocks = new();
    private int _subscriberCount;

    /// <summary>Callers hold the task lease so snapshots and live events cannot interleave.</summary>
    public void Notify(string taskId, StreamResponse streamEvent)
    {
        lock (_subscriberLock)
        {
            if (!_subscribers.TryGetValue(taskId, out var channels))
                return;

            foreach (var channel in channels)
            {
                if (!channel.Writer.TryWrite(streamEvent))
                {
                    channel.Writer.TryComplete(new AgwException(ErrorCodes.A2ASubscriptionLimitExceeded));
                }
                else if (IsTerminalEvent(streamEvent))
                {
                    channel.Writer.TryComplete();
                }
            }
        }
    }

    internal Channel<StreamResponse> CreateChannel(string taskId)
    {
        lock (_subscriberLock)
        {
            if (_subscriberCount >= MaximumSubscribers)
                throw new AgwException(ErrorCodes.A2ASubscriptionLimitExceeded);

            var channel = Channel.CreateBounded<StreamResponse>(
                new BoundedChannelOptions(SubscriberCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                }
            );
            if (!_subscribers.TryGetValue(taskId, out var channels))
                _subscribers[taskId] = channels = [];
            channels.Add(channel);
            _subscriberCount++;
            return channel;
        }
    }

    internal void RemoveChannel(string taskId, Channel<StreamResponse> channel)
    {
        lock (_subscriberLock)
        {
            channel.Writer.TryComplete();
            if (_subscribers.TryGetValue(taskId, out var channels) && channels.Remove(channel))
            {
                _subscriberCount--;
                if (channels.Count == 0)
                    _subscribers.Remove(taskId);
            }
        }
    }

    public async Task<IAsyncDisposable> AcquireTaskLockAsync(
        string taskId,
        CancellationToken cancellationToken = default
    ) => await _taskLocks.AcquireAsync(taskId, cancellationToken).ConfigureAwait(false);

    private static bool IsTerminalEvent(StreamResponse streamEvent)
    {
        var state = streamEvent.StatusUpdate?.Status.State ?? streamEvent.Task?.Status.State;
        return state?.IsTerminal() == true;
    }
}
