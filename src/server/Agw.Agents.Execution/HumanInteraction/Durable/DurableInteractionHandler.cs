using Agw.Agents.Execution.HumanInteraction.Application;

namespace Agw.Agents.Execution.HumanInteraction.Durable;

internal sealed class DurableInteractionHandler : IInteractionHandler
{
    private readonly InteractionPermissionState _permissions;
    private readonly HumanInteractionPolicy _policy;
    private readonly Func<CancellationToken, ValueTask>? _refreshPermissions;
    private readonly List<InteractionRequest> _pending = [];
    private readonly bool _allowInteraction;

    public DurableInteractionHandler(
        InteractionPermissionState permissions,
        HumanInteractionPolicy policy,
        InteractionRequestRegistry requests,
        Func<CancellationToken, ValueTask>? refreshPermissions = null,
        bool allowInteraction = true
    )
    {
        _permissions = permissions;
        _policy = policy;
        _refreshPermissions = refreshPermissions;
        Registry = requests;
        _allowInteraction = allowInteraction;
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
        // 交互不可用时就地拒绝，执行不进入 WaitingForHuman。
        // Decline in place when interaction is unavailable so the execution never enters WaitingForHuman.
        if (!_allowInteraction)
            return new InteractionResolution.Resolved(InteractionRules.Decline(request));
        InteractionRules.RequireInteractiveExecution(request, _policy);
        _pending.Add(request);
        return new InteractionResolution.Pending(request);
    }
}
