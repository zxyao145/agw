using Agw.Extensions.Contracts;
using Agw.Notifications.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Notifications.Application;

public sealed class NotificationChannelCatalog
{
    private readonly IExtensionGenerationCatalog _generations;
    private readonly IExtensionInvoker _invoker;

    public NotificationChannelCatalog(IExtensionGenerationCatalog generations, IExtensionInvoker invoker)
    {
        _generations = generations;
        _invoker = invoker;
    }

    public async Task<IReadOnlyList<NotificationChannelResponse>> ListAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var channels = new List<NotificationChannelResponse>
        {
            new()
            {
                ChannelKey = "desktop",
                Name = "Desktop",
                ExtensionId = null,
                ChannelId = null,
                TargetId = "desktop",
            },
        };
        var active = _generations
            .GetSnapshots()
            .Where(snapshot =>
                snapshot.State == ExtensionGenerationState.Active && snapshot.NotificationChannels.Count > 0
            )
            .ToArray();
        foreach (var snapshot in active)
        {
            await using var generation = await _generations
                .AcquireCurrentAsync(snapshot.GenerationId.ExtensionId, cancellationToken)
                .ConfigureAwait(false);
            foreach (var descriptor in generation.Snapshot.NotificationChannels)
            {
                var result = await _invoker
                    .InvokeNotificationChannelAsync(
                        generation,
                        new ExtensionNotificationChannelInvocation
                        {
                            ChannelId = descriptor.Id,
                            UserId = userId,
                            InvocationId = Guid.CreateVersion7(),
                            Operation = ExtensionNotificationChannelOperation.ListTargets,
                        },
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (result.Targets is null)
                    throw new AgwException(ErrorCodes.ExtensionUnavailable);
                foreach (var target in result.Targets)
                {
                    channels.Add(
                        new NotificationChannelResponse
                        {
                            ChannelKey = $"ext:{snapshot.GenerationId.ExtensionId:D}:{descriptor.Id}:{target.TargetId}",
                            Name = $"{snapshot.Name} / {descriptor.Name} / {target.Name}",
                            ExtensionId = snapshot.GenerationId.ExtensionId,
                            ChannelId = descriptor.Id,
                            TargetId = target.TargetId,
                        }
                    );
                }
            }
        }

        return channels;
    }
}
