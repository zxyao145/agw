namespace Agw.Agents.Execution.HumanInteraction.Application;

/// <summary>Resolve a decision or return a durable boundary; callers never publish requests themselves.</summary>
public interface IInteractionHandler
{
    IInteractionRequestRegistry? Requests => null;
    ValueTask<InteractionResolution> ResolveAsync(InteractionRequest request, CancellationToken cancellationToken);
}

public abstract record InteractionResolution
{
    public sealed record Resolved(InteractionResponse Response) : InteractionResolution;

    public sealed record Pending(InteractionRequest Request) : InteractionResolution;
}
