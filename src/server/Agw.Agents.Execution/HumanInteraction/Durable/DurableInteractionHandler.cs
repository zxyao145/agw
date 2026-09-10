using Agw.Agents.Execution.HumanInteraction.Application;

namespace Agw.Agents.Execution.HumanInteraction.Durable;

internal sealed class DurableInteractionHandler : IInteractionHandler
{
    private readonly InteractionPermissionState _permissions;
    private readonly HumanInteractionPolicy _policy;
    private readonly Func<CancellationToken, ValueTask>? _refreshPermissions;
    private readonly List<InteractionRequest> _pending = [];

    public DurableInteractionHandler(
        InteractionPermissionState permissions,
        HumanInteractionPolicy policy,
        InteractionRequestRegistry requests,
        Func<CancellationToken, ValueTask>? refreshPermissions = null
    )
    {
        _permissions = permissions;
        _policy = policy;
        _refreshPermissions = refreshPermissions;
        Registry = requests;
    }

    public InteractionRequestRegistry Registry { get; }
    public IInteractionRequestRegistry Requests => Registry;
    public IReadOnlyList<InteractionRequest> Pending => _pending;

    public async ValueTask<InteractionResolution> ResolveAsync(
        InteractionRequest request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_refreshPermissions is not null)
            await _refreshPermissions(cancellationToken).ConfigureAwait(false);
        if (InteractionRules.AutomaticallyApprove(request, _permissions.Current) is { } decision)
            return new InteractionResolution.Resolved(decision);
        InteractionRules.RequireInteractiveExecution(request, _policy);
        _pending.Add(request);
        return new InteractionResolution.Pending(request);
    }
}
