using Agw.Auth.Contracts;
using Agw.Extensions.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;

namespace Agw.Events.Application;

public sealed class EventCaller
{
    private readonly IExtensionCallerAccessor _caller;
    private readonly IExtensionGenerationCatalog _generations;

    public EventCaller(IExtensionCallerAccessor caller, IExtensionGenerationCatalog generations)
    {
        _caller = caller;
        _generations = generations;
    }

    public ExtensionGenerationSnapshot RequiredExtension()
    {
        var caller = _caller.Current ?? throw new AgwException(ErrorCodes.EventPublishTypeDenied);
        if (caller.UserId != UserInfoUtil.RequiredUserId)
            throw new AgwException(ErrorCodes.EventPublishTypeDenied);
        return _generations
                .GetSnapshots()
                .SingleOrDefault(snapshot =>
                    snapshot.GenerationId.ExtensionId == caller.ExtensionId
                    && snapshot.GenerationId.GenerationId == caller.GenerationId
                    && snapshot.State is ExtensionGenerationState.Active or ExtensionGenerationState.Draining
                )
            ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
    }

    public void ValidateType(ExtensionGenerationSnapshot extension, string eventType, bool root)
    {
        if (EventTypePatterns.IsReserved(eventType))
            throw new AgwException(ErrorCodes.EventTypeReserved);
        if (root && !extension.EventPermissions.PublishRoot)
            throw new AgwException(ErrorCodes.EventPublishTypeDenied);
        if (!EventTypePatterns.MatchesAny(extension.EventPermissions.Publish, eventType))
            throw new AgwException(ErrorCodes.EventPublishTypeDenied);
    }
}
