namespace Agw.Agents.Execution.HumanInteraction.Application;

internal sealed class UnattendedInteractionHandler : IInteractionHandler
{
    private readonly InteractionPermissionState _permissions;

    public UnattendedInteractionHandler(PermissionMode? mode) => _permissions = new(mode);

    public ValueTask<InteractionResolution> ResolveAsync(
        InteractionRequest request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (InteractionRules.AutomaticallyApprove(request, _permissions.Current) is { } decision)
            return ValueTask.FromResult<InteractionResolution>(new InteractionResolution.Resolved(decision));
        InteractionRules.RequireInteractiveExecution(request, HumanInteractionPolicy.Reject);
        return ValueTask.FromResult<InteractionResolution>(new InteractionResolution.Pending(request));
    }
}
