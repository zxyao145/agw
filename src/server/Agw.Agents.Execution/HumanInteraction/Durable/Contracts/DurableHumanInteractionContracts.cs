namespace Agw.Agents.Execution.HumanInteraction.Durable.Contracts;

/// <summary>The decision belongs to exactly one persisted request.</summary>
internal sealed record DurableResolvedInteraction(InteractionRequest Request, InteractionResponse Response);

internal sealed record SubmitDurableHumanResponseRequest(Guid ExecutionId, InteractionResponse Response);

/// <summary>Persisted in the existing interaction JSON column; no SDK objects or delegates.</summary>
internal sealed record DurableInteractionState
{
    public IReadOnlyList<InteractionRequest> Pending { get; init; } = [];
    public IReadOnlyList<UserInputInteraction> InputCatalog { get; init; } = [];
    public IReadOnlyList<DurableResolvedInteraction> ResolvedInputs { get; init; } = [];
}
