using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.HumanInteraction.Application;

internal static class InteractionResults
{
    public static InteractionResponse RequireResolved(InteractionResolution resolution) =>
        resolution switch
        {
            InteractionResolution.Resolved resolved => resolved.Response,
            _ => throw new AgwException(
                ErrorCodes.InvalidParam,
                "This execution path cannot return a durable interaction boundary."
            ),
        };
}
