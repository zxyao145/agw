using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;

namespace Agw.Infrastructure.Events.Sources;

public sealed class ExtensionEventSourcePoller
{
    private readonly IExtensionGenerationCatalog _generationCatalog;
    private readonly IExtensionInvoker _invoker;

    public ExtensionEventSourcePoller(IExtensionGenerationCatalog generationCatalog, IExtensionInvoker invoker)
    {
        _generationCatalog = generationCatalog;
        _invoker = invoker;
    }

    public async Task<ExtensionSourcePollResult> PollAsync(
        EventSource source,
        bool baseline,
        CancellationToken cancellationToken
    )
    {
        var providerId = source.ProviderId;
        var ownerId = source.CreateBy;
        if (
            source.Kind != EventSourceKind.Provider
            || source.ProviderExtensionId is not { } extensionId
            || string.IsNullOrWhiteSpace(providerId)
            || string.IsNullOrWhiteSpace(ownerId)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Event source provider identity is incomplete.");
        }

        using var configDocument = JsonDocument.Parse(source.ConfigJson);
        JsonElement? cursor = source.CursorJson is null ? null : ParseCursor(source.CursorJson);
        await using var lease = await _generationCatalog.AcquireCurrentAsync(extensionId, cancellationToken);
        if (!lease.Snapshot.EventSourceProviders.Any(provider => provider.Id == providerId))
        {
            throw new AgwException(ErrorCodes.EventSourceNotFound);
        }

        return await _invoker.InvokeSourcePollAsync(
            lease,
            new ExtensionSourcePollInvocation
            {
                ProviderId = providerId,
                UserId = ownerId,
                InvocationId = Guid.CreateVersion7(),
                Request = new ExtensionSourcePollRequest
                {
                    SourceId = source.Id,
                    Config = configDocument.RootElement.Clone(),
                    Cursor = cursor,
                    Baseline = baseline,
                },
            },
            cancellationToken
        );
    }

    private static JsonElement ParseCursor(string cursorJson)
    {
        using var document = JsonDocument.Parse(cursorJson);
        return document.RootElement.Clone();
    }
}
